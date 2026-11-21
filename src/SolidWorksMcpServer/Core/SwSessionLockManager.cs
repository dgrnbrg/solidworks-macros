using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Core
{
    /// <summary>
    /// Cross-process exclusive session lock for SOLIDWORKS.
    /// Ensures only one AI agent (identified by agent_id / Conversation ID) may mutate or
    /// query the shared SOLIDWORKS instance at a time, automatically clears orphaned/expired
    /// locks on supervisor crashes or idle timeouts, and supports user-authorized force_claim /
    /// force_unlock (including killing a hung --worker subprocess).
    /// </summary>
    public static class SwSessionLockManager
    {
        private const string StateMutexName = @"Local\SolidWorksMcpSessionLockStateMutex";
        public const int DefaultLeaseTtlSeconds = 900;

        public sealed class SessionLockState
        {
            public bool IsLocked;
            public string AgentId;
            public string Task;
            public int SupervisorPid;
            public int WorkerPid;
            public string InFlightTool;
            public string InFlightStartedUtc;
            public string ClaimedAtUtc;
            public string LastActivityUtc;
            public string LastTool;
            public int LeaseTtlSeconds = DefaultLeaseTtlSeconds;
            public long Generation;
            public string LastNote;
        }

        private sealed class HealthInfo
        {
            public bool OwnerProcessAlive;
            public bool WorkerProcessAlive;
            public double IdleSeconds;
            public bool IsExpired;
            public double RemainingTtlSeconds;
        }

        public static string GetLockFilePath()
        {
            string overrideDir = System.Environment.GetEnvironmentVariable("SOLIDWORKS_MCP_LOCK_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                if (!Directory.Exists(overrideDir))
                    Directory.CreateDirectory(overrideDir);
                return Path.Combine(overrideDir, "session_lock.json");
            }

            string dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SolidWorksMcpServer");
            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return Path.Combine(dir, "session_lock.json");
            }
            catch
            {
                string fallback = Path.Combine(Path.GetTempPath(), "SolidWorksMcpServer");
                if (!Directory.Exists(fallback))
                    Directory.CreateDirectory(fallback);
                return Path.Combine(fallback, "session_lock.json");
            }
        }

        public static int GetCurrentSupervisorPid()
        {
            string envPid = System.Environment.GetEnvironmentVariable("SOLIDWORKS_MCP_SUPERVISOR_PID");
            if (!string.IsNullOrWhiteSpace(envPid) &&
                int.TryParse(envPid.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                parsed > 0)
            {
                return parsed;
            }
            return Process.GetCurrentProcess().Id;
        }

        public static Dictionary<string, object> GetStatusPayload()
        {
            return WithStateMutex(() =>
            {
                SessionLockState state = LoadAndNormalizeState(out HealthInfo health, saveIfChanged: true);
                return BuildStateDict(state, health);
            });
        }

        public static bool IsLockedByOtherAgent(string callerAgentId)
        {
            string trimmed = (callerAgentId ?? string.Empty).Trim();
            return WithStateMutex(() =>
            {
                SessionLockState state = LoadAndNormalizeState(out _, saveIfChanged: true);
                if (!state.IsLocked)
                    return false;
                if (string.IsNullOrEmpty(trimmed))
                    return true;
                return !string.Equals(state.AgentId ?? string.Empty, trimmed, StringComparison.OrdinalIgnoreCase);
            });
        }

        public static bool ShouldBypassComMutexForConnectStatus(Dictionary<string, object> args)
        {
            bool launch = args != null && JsonHelper.GetBool(args, "launch_if_closed", false);
            bool restart = args != null && JsonHelper.GetBool(args, "restart_if_running", false);
            if (!launch && !restart)
                return true;

            return false;
        }

        public static bool TryClaim(
            string agentId,
            string task,
            int? leaseTtlSeconds,
            bool force,
            bool abortRunningWorker,
            out Dictionary<string, object> resultPayload,
            out bool isError)
        {
            Dictionary<string, object> outPayload = null;
            bool outErr = false;

            WithStateMutex<int>(() =>
            {
                outPayload = TryClaimCore(agentId, task, leaseTtlSeconds, force, abortRunningWorker, out outErr);
                return 0;
            });

            resultPayload = outPayload;
            isError = outErr;
            return !isError;
        }

        public static bool Release(
            string agentId,
            bool force,
            bool abortRunningWorker,
            out Dictionary<string, object> resultPayload,
            out bool isError)
        {
            Dictionary<string, object> outPayload = null;
            bool outErr = false;

            WithStateMutex<int>(() =>
            {
                outPayload = ReleaseCore(agentId, force, abortRunningWorker, out outErr);
                return 0;
            });

            resultPayload = outPayload;
            isError = outErr;
            return !isError;
        }

        public static bool IsToolExemptFromLock(string toolName, Dictionary<string, object> args)
        {
            if (string.Equals(toolName, "sw_get_shop_profile", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(toolName, "sw_execute_csharp", StringComparison.OrdinalIgnoreCase) &&
                args != null && JsonHelper.GetBool(args, "compile_only", false))
                return true;

            if (string.Equals(toolName, "sw_connect_status", StringComparison.OrdinalIgnoreCase))
            {
                string lockAction = args != null
                    ? (JsonHelper.GetString(args, "lock_action", "status") ?? "status").Trim().ToLowerInvariant()
                    : "status";
                bool mutatesSession = args != null &&
                    (JsonHelper.GetBool(args, "launch_if_closed", false) ||
                     JsonHelper.GetBool(args, "restart_if_running", false));

                if (!mutatesSession)
                    return true;

                if (lockAction == "force_claim" || lockAction == "force_unlock")
                    return true;
            }

            return false;
        }

        public static bool TryEnterToolCall(
            string toolName,
            Dictionary<string, object> args,
            int workerPid,
            out long generation,
            out McpToolResult blockedError)
        {
            if (IsToolExemptFromLock(toolName, args))
            {
                generation = 0;
                blockedError = null;
                return true;
            }

            long outGen = 0;
            McpToolResult outErr = null;
            bool allowed = WithStateMutex(() =>
            {
                SessionLockState state = LoadAndNormalizeState(out HealthInfo health, saveIfChanged: true);
                string callerAgentId = args != null ? (JsonHelper.GetString(args, "agent_id", string.Empty) ?? string.Empty).Trim() : string.Empty;

                if (!state.IsLocked)
                {
                    if (string.Equals(toolName, "sw_connect_status", StringComparison.OrdinalIgnoreCase))
                    {
                        string lockAction = (JsonHelper.GetString(args, "lock_action", "status") ?? "status").Trim().ToLowerInvariant();
                        if (lockAction == "claim" || lockAction == "force_claim")
                        {
                            string claimTask = JsonHelper.GetString(args, "task");
                            int? ttlOpt = args != null && args.ContainsKey("lease_ttl_seconds")
                                ? (int?)JsonHelper.GetInt(args, "lease_ttl_seconds", DefaultLeaseTtlSeconds)
                                : null;
                            Dictionary<string, object> claimRes = TryClaimCore(
                                callerAgentId, claimTask, ttlOpt, force: lockAction == "force_claim", abortRunningWorker: false, out bool claimErr);
                            if (claimErr)
                            {
                                outErr = McpToolResult.JsonText(claimRes, isError: true);
                                return false;
                            }
                            state = LoadAndNormalizeState(out health, saveIfChanged: false);
                        }
                    }
                }

                if (!state.IsLocked)
                {
                    outErr = McpToolResult.Error(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "SOLIDWORKS session lock is not claimed. Before calling '{0}', you MUST claim exclusive access to SOLIDWORKS by calling sw_connect_status(lock_action='claim', agent_id='<your Conversation ID>', task='<brief summary of your task>'), pass that same agent_id on your tool calls, and release it via sw_connect_status(lock_action='release', agent_id='...') when finished.",
                            toolName),
                        new Dictionary<string, object>
                        {
                            { "lock_required", true },
                            { "is_locked", false },
                            { "tool_name", toolName },
                            { "session_lock", BuildStateDict(state, health) }
                        });
                    return false;
                }

                if (string.IsNullOrEmpty(callerAgentId))
                {
                    outErr = McpToolResult.Error(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "SOLIDWORKS is currently locked by agent_id='{0}' (task: '{1}'). If you ARE agent '{0}', re-issue '{2}' with \"agent_id\": \"{0}\". If you are a DIFFERENT agent, STOP immediately — do NOT call SOLIDWORKS tools and do NOT override the lock; tell the user that SOLIDWORKS is in use by another agent ('{0}': {1}) and ask whether to work on something else, wait, or force-override the lock.",
                            state.AgentId, state.Task ?? "unspecified", toolName),
                        new Dictionary<string, object>
                        {
                            { "locked_by_other_agent", true },
                            { "holder_agent_id", state.AgentId },
                            { "holder_task", state.Task },
                            { "tool_name", toolName },
                            { "session_lock", BuildStateDict(state, health) }
                        });
                    return false;
                }

                if (!string.Equals(state.AgentId ?? string.Empty, callerAgentId, StringComparison.OrdinalIgnoreCase))
                {
                    outErr = McpToolResult.Error(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "SOLIDWORKS is currently in use and locked by another agent (agent_id='{0}', task='{1}', idle={2:F0}s, in_flight_tool='{3}'). Your agent_id='{4}' does not hold the lock. STOP immediately: do NOT call SOLIDWORKS tools and do NOT force-unlock without user permission. Tell the user that SOLIDWORKS is in use by another agent and ask whether they want you to work on something else, wait, or force-override the lock (sw_connect_status with lock_action='force_claim' or 'force_unlock').",
                            state.AgentId, state.Task ?? "unspecified", health.IdleSeconds, state.InFlightTool ?? "none", callerAgentId),
                        new Dictionary<string, object>
                        {
                            { "locked_by_other_agent", true },
                            { "holder_agent_id", state.AgentId },
                            { "holder_task", state.Task },
                            { "caller_agent_id", callerAgentId },
                            { "tool_name", toolName },
                            { "session_lock", BuildStateDict(state, health) }
                        });
                    return false;
                }

                string nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                state.SupervisorPid = GetCurrentSupervisorPid();
                state.WorkerPid = workerPid > 0 ? workerPid : Process.GetCurrentProcess().Id;
                state.InFlightTool = toolName;
                state.InFlightStartedUtc = nowIso;
                state.LastActivityUtc = nowIso;
                state.LastTool = toolName;
                SaveState(state);
                outGen = state.Generation;
                return true;
            });

            generation = outGen;
            blockedError = outErr;
            return allowed;
        }

        public static void ExitToolCall(long generation)
        {
            if (generation <= 0)
                return;

            WithStateMutex<int>(() =>
            {
                SessionLockState state = LoadRawState();
                if (state.IsLocked && state.Generation == generation)
                {
                    state.InFlightTool = null;
                    state.InFlightStartedUtc = null;
                    state.WorkerPid = 0;
                    state.LastActivityUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    SaveState(state);
                }
                return 0;
            });
        }

        public static void NotifyWorkerCrashed(int crashedWorkerPid, string toolName, string exitHex)
        {
            WithStateMutex<int>(() =>
            {
                SessionLockState state = LoadRawState();
                if (state.IsLocked)
                {
                    state.InFlightTool = null;
                    state.InFlightStartedUtc = null;
                    if (state.WorkerPid == crashedWorkerPid || crashedWorkerPid <= 0)
                        state.WorkerPid = 0;
                    state.LastActivityUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    state.LastNote = string.Format(
                        CultureInfo.InvariantCulture,
                        "Worker PID {0} terminated during '{1}' ({2}).",
                        crashedWorkerPid, toolName ?? "unknown", exitHex ?? "unknown");
                    SaveState(state);
                }
                return 0;
            });
        }

        private static Dictionary<string, object> TryClaimCore(
            string agentId,
            string task,
            int? leaseTtlSeconds,
            bool force,
            bool abortRunningWorker,
            out bool isError)
        {
            string trimmedAgent = (agentId ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmedAgent))
            {
                SessionLockState cur = LoadAndNormalizeState(out HealthInfo curHealth, saveIfChanged: true);
                isError = true;
                return new Dictionary<string, object>
                {
                    { "lock_acquired", false },
                    { "error", "agent_id is required to claim the SOLIDWORKS session lock (pass your Conversation ID as agent_id)." },
                    { "session_lock", BuildStateDict(cur, curHealth) }
                };
            }

            SessionLockState state = LoadAndNormalizeState(out HealthInfo health, saveIfChanged: true);
            string reclaimedFromNote = null;
            bool killedWorker = false;
            int killedPid = 0;
            string prevHolder = state.AgentId;

            if (state.IsLocked &&
                !string.Equals(state.AgentId ?? string.Empty, trimmedAgent, StringComparison.OrdinalIgnoreCase))
            {
                if (!force)
                {
                    isError = true;
                    string errMsg = string.Format(
                        CultureInfo.InvariantCulture,
                        "SOLIDWORKS is currently locked by another agent (agent_id='{0}', task='{1}', idle={2:F0}s, in_flight_tool='{3}'). STOP immediately: do NOT call SOLIDWORKS tools and do NOT force-override without user permission. Tell the user that SOLIDWORKS is in use by another agent and ask whether they want you to work on something else, wait, or force-override the lock (sw_connect_status with lock_action='force_claim' or 'force_unlock').",
                        state.AgentId, state.Task ?? "unspecified", health.IdleSeconds, state.InFlightTool ?? "none");
                    return new Dictionary<string, object>
                    {
                        { "lock_acquired", false },
                        { "locked_by_other_agent", true },
                        { "holder_agent_id", state.AgentId },
                        { "holder_task", state.Task },
                        { "claimed_at_utc", state.ClaimedAtUtc },
                        { "last_activity_utc", state.LastActivityUtc },
                        { "last_tool", state.LastTool },
                        { "in_flight_tool", state.InFlightTool },
                        { "idle_seconds", Math.Round(health.IdleSeconds, 1) },
                        { "error", errMsg },
                        { "session_lock", BuildStateDict(state, health) }
                    };
                }

                if (abortRunningWorker && state.WorkerPid > 0)
                {
                    killedWorker = TryKillWorkerProcess(state.WorkerPid);
                    if (killedWorker)
                        killedPid = state.WorkerPid;
                }

                reclaimedFromNote = killedWorker
                    ? string.Format(CultureInfo.InvariantCulture, "Force-claimed from '{0}' (terminated worker PID {1}).", prevHolder, killedPid)
                    : string.Format(CultureInfo.InvariantCulture, "Force-claimed from '{0}'.", prevHolder);
            }

            bool isRenewal = state.IsLocked &&
                string.Equals(state.AgentId ?? string.Empty, trimmedAgent, StringComparison.OrdinalIgnoreCase);

            string nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            int ttl = leaseTtlSeconds.HasValue && leaseTtlSeconds.Value > 0
                ? leaseTtlSeconds.Value
                : (state.LeaseTtlSeconds > 0 ? state.LeaseTtlSeconds : DefaultLeaseTtlSeconds);

            state.IsLocked = true;
            state.AgentId = trimmedAgent;
            if (!string.IsNullOrWhiteSpace(task))
                state.Task = task.Trim();
            else if (!isRenewal)
                state.Task = "SOLIDWORKS task";
            state.SupervisorPid = GetCurrentSupervisorPid();
            if (!isRenewal)
            {
                state.ClaimedAtUtc = nowIso;
                state.Generation++;
            }
            else if (state.Generation <= 0)
            {
                state.Generation = 1;
            }
            state.WorkerPid = 0;
            state.InFlightTool = null;
            state.InFlightStartedUtc = null;
            state.LastActivityUtc = nowIso;
            state.LastTool = force ? "sw_connect_status(force_claim)" : "sw_connect_status(claim)";
            state.LeaseTtlSeconds = ttl;
            if (!string.IsNullOrEmpty(reclaimedFromNote))
                state.LastNote = reclaimedFromNote;
            else if (!isRenewal && string.IsNullOrEmpty(state.LastNote))
                state.LastNote = "Claimed by '" + trimmedAgent + "'.";

            SaveState(state);
            health = EvaluateHealth(state);
            isError = false;

            return new Dictionary<string, object>
            {
                { "lock_acquired", true },
                { "renewed", isRenewal },
                { "forced", force },
                { "previous_agent_id", (!isRenewal && force) ? prevHolder : null },
                { "aborted_worker_pid", killedWorker ? (int?)killedPid : null },
                { "session_lock", BuildStateDict(state, health) }
            };
        }

        private static Dictionary<string, object> ReleaseCore(
            string agentId,
            bool force,
            bool abortRunningWorker,
            out bool isError)
        {
            SessionLockState state = LoadAndNormalizeState(out HealthInfo health, saveIfChanged: true);
            string trimmedAgent = (agentId ?? string.Empty).Trim();

            if (!state.IsLocked)
            {
                isError = false;
                return new Dictionary<string, object>
                {
                    { "lock_released", true },
                    { "was_already_unlocked", true },
                    { "forced", force },
                    { "session_lock", BuildStateDict(state, health) }
                };
            }

            if (!force)
            {
                if (string.IsNullOrEmpty(trimmedAgent))
                {
                    isError = true;
                    return new Dictionary<string, object>
                    {
                        { "lock_released", false },
                        { "error", string.Format(CultureInfo.InvariantCulture, "SOLIDWORKS lock is held by agent_id='{0}'. Pass matching agent_id to release cleanly, or pass lock_action='force_unlock' if authorized to override.", state.AgentId) },
                        { "session_lock", BuildStateDict(state, health) }
                    };
                }

                if (!string.Equals(state.AgentId ?? string.Empty, trimmedAgent, StringComparison.OrdinalIgnoreCase))
                {
                    isError = true;
                    return new Dictionary<string, object>
                    {
                        { "lock_released", false },
                        { "locked_by_other_agent", true },
                        { "holder_agent_id", state.AgentId },
                        { "error", string.Format(CultureInfo.InvariantCulture, "Cannot release SOLIDWORKS lock held by '{0}' using agent_id='{1}'. Ask the user before using lock_action='force_unlock'.", state.AgentId, trimmedAgent) },
                        { "session_lock", BuildStateDict(state, health) }
                    };
                }
            }

            bool killedWorker = false;
            int killedPid = 0;
            if (force && abortRunningWorker && state.WorkerPid > 0)
            {
                killedWorker = TryKillWorkerProcess(state.WorkerPid);
                if (killedWorker)
                    killedPid = state.WorkerPid;
            }

            string prevAgent = state.AgentId;
            state.IsLocked = false;
            state.InFlightTool = null;
            state.InFlightStartedUtc = null;
            state.WorkerPid = 0;
            state.Generation++;
            state.LastActivityUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            state.LastTool = force ? "sw_connect_status(force_unlock)" : "sw_connect_status(release)";
            state.LastNote = force
                ? (killedWorker
                    ? string.Format(CultureInfo.InvariantCulture, "Force-unlocked from '{0}' (terminated worker PID {1}).", prevAgent, killedPid)
                    : string.Format(CultureInfo.InvariantCulture, "Force-unlocked from '{0}'.", prevAgent))
                : string.Format(CultureInfo.InvariantCulture, "Released cleanly by '{0}'.", prevAgent);

            SaveState(state);
            health = EvaluateHealth(state);
            isError = false;

            return new Dictionary<string, object>
            {
                { "lock_released", true },
                { "was_already_unlocked", false },
                { "previous_agent_id", prevAgent },
                { "forced", force },
                { "aborted_worker_pid", killedWorker ? (int?)killedPid : null },
                { "session_lock", BuildStateDict(state, health) }
            };
        }

        private static SessionLockState LoadAndNormalizeState(out HealthInfo health, bool saveIfChanged)
        {
            SessionLockState state = LoadRawState();
            bool changed = false;

            bool ownerAlive = state.SupervisorPid <= 0 || IsProcessAlive(state.SupervisorPid);
            bool workerAlive = state.WorkerPid > 0 && IsProcessAlive(state.WorkerPid);

            if (!string.IsNullOrEmpty(state.InFlightTool) && state.WorkerPid > 0 && !workerAlive)
            {
                state.LastNote = string.Format(
                    CultureInfo.InvariantCulture,
                    "Cleared stale in-flight tool '{0}' after worker PID {1} exited.",
                    state.InFlightTool, state.WorkerPid);
                state.InFlightTool = null;
                state.InFlightStartedUtc = null;
                state.WorkerPid = 0;
                changed = true;
            }

            health = EvaluateHealth(state);

            if (state.IsLocked && state.SupervisorPid > 0 && !ownerAlive)
            {
                string prevAgent = state.AgentId;
                int deadPid = state.SupervisorPid;
                bool killedOrphanWorker = false;
                int orphanWorkerPid = state.WorkerPid;
                if (orphanWorkerPid > 0 && workerAlive)
                    killedOrphanWorker = TryKillWorkerProcess(orphanWorkerPid);

                state.IsLocked = false;
                state.InFlightTool = null;
                state.InFlightStartedUtc = null;
                state.WorkerPid = 0;
                state.Generation++;
                state.LastNote = killedOrphanWorker
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Auto-freed orphaned lock from dead supervisor PID {0} (agent '{1}') and terminated orphaned worker PID {2}.",
                        deadPid, prevAgent ?? "unknown", orphanWorkerPid)
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "Auto-freed orphaned lock from dead supervisor PID {0} (agent '{1}').",
                        deadPid, prevAgent ?? "unknown");
                changed = true;
                health = EvaluateHealth(state);
            }
            else if (state.IsLocked && health.IsExpired)
            {
                string prevAgent = state.AgentId;
                double idleSec = health.IdleSeconds;
                int ttl = state.LeaseTtlSeconds > 0 ? state.LeaseTtlSeconds : DefaultLeaseTtlSeconds;
                state.IsLocked = false;
                state.InFlightTool = null;
                state.InFlightStartedUtc = null;
                state.WorkerPid = 0;
                state.Generation++;
                state.LastNote = string.Format(
                    CultureInfo.InvariantCulture,
                    "Auto-expired idle lock held by '{0}' after {1:F0}s inactivity (TTL={2}s).",
                    prevAgent ?? "unknown", idleSec, ttl);
                changed = true;
                health = EvaluateHealth(state);
            }

            if (changed && saveIfChanged)
                SaveState(state);

            return state;
        }

        private static HealthInfo EvaluateHealth(SessionLockState state)
        {
            bool ownerAlive = state.SupervisorPid <= 0 || IsProcessAlive(state.SupervisorPid);
            bool workerAlive = state.WorkerPid > 0 && IsProcessAlive(state.WorkerPid);
            double idleSec = ComputeIdleSeconds(state);
            int ttl = state.LeaseTtlSeconds > 0 ? state.LeaseTtlSeconds : DefaultLeaseTtlSeconds;
            bool hasInFlight = state.IsLocked && !string.IsNullOrEmpty(state.InFlightTool) && workerAlive;
            bool isExpired = state.IsLocked && !hasInFlight && idleSec > ttl;

            return new HealthInfo
            {
                OwnerProcessAlive = ownerAlive,
                WorkerProcessAlive = workerAlive,
                IdleSeconds = idleSec,
                IsExpired = isExpired,
                RemainingTtlSeconds = state.IsLocked ? Math.Max(0.0, ttl - idleSec) : 0.0
            };
        }

        private static double ComputeIdleSeconds(SessionLockState state)
        {
            string ts = !string.IsNullOrEmpty(state.LastActivityUtc) ? state.LastActivityUtc : state.ClaimedAtUtc;
            if (string.IsNullOrWhiteSpace(ts))
                return 0.0;

            if (DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dt))
            {
                DateTime utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                return Math.Max(0.0, (DateTime.UtcNow - utc).TotalSeconds);
            }
            return 0.0;
        }

        private static Dictionary<string, object> BuildStateDict(SessionLockState state, HealthInfo health)
        {
            int ttl = state.LeaseTtlSeconds > 0 ? state.LeaseTtlSeconds : DefaultLeaseTtlSeconds;
            return new Dictionary<string, object>
            {
                { "is_locked", state.IsLocked },
                { "agent_id", state.IsLocked ? state.AgentId : null },
                { "task", state.IsLocked ? state.Task : null },
                { "supervisor_pid", state.IsLocked ? (int?)state.SupervisorPid : null },
                { "worker_pid", (state.IsLocked && state.WorkerPid > 0) ? (int?)state.WorkerPid : null },
                { "in_flight_tool", state.IsLocked ? state.InFlightTool : null },
                { "in_flight_started_utc", state.IsLocked ? state.InFlightStartedUtc : null },
                { "claimed_at_utc", state.IsLocked ? state.ClaimedAtUtc : null },
                { "last_activity_utc", state.LastActivityUtc },
                { "last_tool", state.LastTool },
                { "idle_seconds", Math.Round(health.IdleSeconds, 1) },
                { "lease_ttl_seconds", ttl },
                { "remaining_ttl_seconds", Math.Round(health.RemainingTtlSeconds, 1) },
                { "generation", state.Generation },
                { "last_note", state.LastNote },
                { "lock_file", GetLockFilePath() }
            };
        }

        private static SessionLockState LoadRawState()
        {
            string path = GetLockFilePath();
            if (!File.Exists(path))
                return new SessionLockState();

            try
            {
                string raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw))
                    return new SessionLockState();

                Dictionary<string, object> d = JsonHelper.DeserializeDict(raw);
                SessionLockState s = new SessionLockState
                {
                    IsLocked = JsonHelper.GetBool(d, "is_locked", false),
                    AgentId = JsonHelper.GetString(d, "agent_id"),
                    Task = JsonHelper.GetString(d, "task"),
                    SupervisorPid = JsonHelper.GetInt(d, "supervisor_pid", 0),
                    WorkerPid = JsonHelper.GetInt(d, "worker_pid", 0),
                    InFlightTool = JsonHelper.GetString(d, "in_flight_tool"),
                    InFlightStartedUtc = JsonHelper.GetString(d, "in_flight_started_utc"),
                    ClaimedAtUtc = JsonHelper.GetString(d, "claimed_at_utc"),
                    LastActivityUtc = JsonHelper.GetString(d, "last_activity_utc"),
                    LastTool = JsonHelper.GetString(d, "last_tool"),
                    LeaseTtlSeconds = JsonHelper.GetInt(d, "lease_ttl_seconds", DefaultLeaseTtlSeconds),
                    LastNote = JsonHelper.GetString(d, "last_note")
                };

                if (d.TryGetValue("generation", out object genObj) && genObj != null)
                {
                    long.TryParse(Convert.ToString(genObj, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out long gen);
                    s.Generation = gen;
                }

                if (s.LeaseTtlSeconds <= 0)
                    s.LeaseTtlSeconds = DefaultLeaseTtlSeconds;

                return s;
            }
            catch
            {
                return new SessionLockState();
            }
        }

        private static void SaveState(SessionLockState state)
        {
            string path = GetLockFilePath();
            Dictionary<string, object> d = new Dictionary<string, object>
            {
                { "is_locked", state.IsLocked },
                { "agent_id", state.AgentId },
                { "task", state.Task },
                { "supervisor_pid", state.SupervisorPid },
                { "worker_pid", state.WorkerPid },
                { "in_flight_tool", state.InFlightTool },
                { "in_flight_started_utc", state.InFlightStartedUtc },
                { "claimed_at_utc", state.ClaimedAtUtc },
                { "last_activity_utc", state.LastActivityUtc },
                { "last_tool", state.LastTool },
                { "lease_ttl_seconds", state.LeaseTtlSeconds },
                { "generation", state.Generation },
                { "last_note", state.LastNote }
            };

            string json = JsonHelper.Serialize(d);
            string tmp = path + ".tmp." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(tmp, json);
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                File.WriteAllText(path, json);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryKillWorkerProcess(int pid)
        {
            if (pid <= 0 || pid == Process.GetCurrentProcess().Id)
                return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited)
                        return false;
                    if (!p.ProcessName.StartsWith("SolidWorksMcpServer", StringComparison.OrdinalIgnoreCase))
                        return false;
                    p.Kill();
                    p.WaitForExit(1500);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static T WithStateMutex<T>(Func<T> action)
        {
            using (Mutex mutex = new Mutex(false, StateMutexName))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(15000);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                        throw new TimeoutException("Timed out waiting for SOLIDWORKS session lock state mutex.");

                    return action();
                }
                finally
                {
                    if (acquired)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }
    }
}
