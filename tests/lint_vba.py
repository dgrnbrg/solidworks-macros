"""Static VBA pre-flight linter for SOLIDWORKS macro .bas files.

Catches common compile-time traps that VBA's default "Compile On Demand"
setting can hide until runtime, including:
- Reserved VBA keywords used as identifiers (e.g., `oR` colliding with `Or`)
- Missing `Option Explicit`, duplicate `Dim`s, or `Type` after procedures
- Unbalanced procedures or control-flow blocks (`If`/`For`/`Do`/`Select`/`With`)
- Undeclared local identifiers / loop variables under `Option Explicit`
- `ByRef` parameter mismatches (expressions, literals, `Nothing`, `Variant` lookups)
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from typing import Dict, List, Set, Tuple

# Reserved VBA keywords that are illegal as variable, parameter, Const, or Type-field names.
RESERVED_KEYWORDS: Set[str] = {
    "and", "as", "boolean", "byref", "byte", "byval", "call", "case",
    "cbool", "cbyte", "ccur", "cdate", "cdbl", "cdec", "cint", "circle",
    "clng", "clnglng", "clngptr", "close", "compare", "const", "csng",
    "cstr", "cur", "currency", "cvar", "cverr", "date", "decimal", "declare",
    "defbool", "defbyte", "defcur", "defdate", "defdbl", "defdec", "defint",
    "deflng", "deflnglng", "deflngptr", "defobj", "defsng", "defstr", "defvar",
    "dim", "do", "double", "each", "else", "elseif", "empty", "end", "endif",
    "enum", "eqv", "erase", "error", "event", "exit", "explicit", "false",
    "fix", "for", "friend", "function", "get", "global", "gosub", "goto",
    "if", "imp", "implements", "in", "input", "integer", "interface", "is",
    "len", "let", "lib", "like", "line", "load", "local", "lock", "long",
    "longlong", "longptr", "loop", "lset", "me", "mid", "mod", "module",
    "name", "new", "next", "not", "nothing", "null", "object", "on", "open",
    "option", "optional", "or", "output", "paramarray", "preserve", "print",
    "private", "property", "pset", "public", "put", "raiseevent", "random",
    "randomize", "read", "redim", "rem", "resume", "return", "rset", "scale",
    "seek", "select", "set", "shared", "single", "spc", "static", "step",
    "stop", "string", "sub", "tab", "text", "then", "to", "true", "type",
    "typeof", "unload", "unlock", "until", "variant", "wend", "while",
    "width", "with", "withevents", "write", "xor",
}

# Built-in VBA / SOLIDWORKS identifiers valid inside procedure bodies.
BUILTIN_IDENTIFIERS: Set[str] = RESERVED_KEYWORDS | {
    "trim", "lcase", "ucase", "left", "right", "instr", "instrrev", "replace",
    "split", "join", "val", "str", "isarray", "isnumeric", "isempty",
    "isobject", "typename", "iif", "msgbox", "inputbox", "createobject",
    "environ", "format", "timer", "int", "rnd", "sqr", "abs", "sgn", "tan",
    "cos", "sin", "atn", "exp", "log", "hex", "ubound", "lbound", "array",
    "err", "doevents", "strcomp", "round", "collection",
    "vbcrlf", "vblf", "vbcr", "vbnewline", "vbtab", "vbexclamation",
    "vbinformation", "vbcritical", "vbquestion", "vbokonly", "vbtextcompare",
    "application", "sldworks", "modeldoc2", "partdoc", "assemblydoc",
    "face2", "body2", "entity", "surface", "component2", "selectionmgr",
    "feature", "configuration", "swconst", "swopendocoptions_readonly",
    "scripting", "shell",
}


def strip_vba_comments_and_strings(raw_line: str, mask_strings: bool = True) -> str:
    """Strip inline comments and blank out string literal interiors."""
    out: List[str] = []
    in_str = False
    i = 0
    while i < len(raw_line):
        ch = raw_line[i]
        if ch == '"':
            if in_str and i + 1 < len(raw_line) and raw_line[i + 1] == '"':
                out.append("xx" if mask_strings else '""')
                i += 2
                continue
            in_str = not in_str
            out.append('"')
        elif ch == "'" and not in_str:
            break
        elif in_str:
            out.append(" " if not mask_strings else ("x" if ch not in "()," else "_"))
        else:
            out.append(ch)
        i += 1
    return "".join(out).strip()


def join_continuations(lines: List[str], mask_strings: bool = True) -> List[Tuple[int, str]]:
    """Join VBA underscore (`_`) line continuations into logical lines."""
    joined: List[Tuple[int, str]] = []
    buf = ""
    start_ln = 1
    for idx, raw in enumerate(lines, 1):
        code = strip_vba_comments_and_strings(raw, mask_strings=mask_strings)
        if not buf:
            start_ln = idx
        if code.endswith(" _") or code == "_":
            buf += (" " if buf else "") + code[:-1].rstrip()
        else:
            full = (buf + " " + code).strip() if buf else code
            joined.append((start_ln, full))
            buf = ""
    if buf:
        joined.append((start_ln, buf))
    return joined


def split_statements(logical_line: str) -> List[str]:
    """Split a logical VBA line on colon statement separators."""
    return [part.strip() for part in logical_line.split(":") if part.strip()]


def parse_params(params_raw: str) -> List[Tuple[str, bool, str, bool]]:
    """Return list of (name, is_byval, type_str, is_optional)."""
    params: List[Tuple[str, bool, str, bool]] = []
    if not params_raw.strip():
        return params
    for raw_p in params_raw.split(","):
        p = raw_p.strip()
        is_opt = False
        if re.match(r"^Optional\b", p, re.I):
            is_opt = True
            p = re.sub(r"^Optional\s+", "", p, flags=re.I).strip()
        is_byval = bool(re.match(r"^ByVal\b", p, re.I))
        p_clean = re.sub(r"^(?:ByVal|ByRef)\s+", "", p, flags=re.I).strip()
        p_no_default = p_clean.split("=")[0].strip()
        m = re.match(r"^([A-Za-z_]\w*)(\(\s*\))?\s*(?:As\s+([A-Za-z0-9_.]+))?$", p_no_default, re.I)
        if m:
            pname = m.group(1)
            is_arr = bool(m.group(2))
            ptype = (m.group(3) or "Variant") + ("()" if is_arr else "")
            params.append((pname, is_byval, ptype, is_opt))
        else:
            params.append((p_no_default, is_byval, "Variant", is_opt))
    return params


def split_top_level_args(arg_str: str) -> List[str]:
    """Split comma-separated call arguments respecting nested parentheses."""
    if not arg_str.strip():
        return []
    args: List[str] = []
    cur: List[str] = []
    depth = 0
    for ch in arg_str:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        if ch == "," and depth == 0:
            args.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
    tail = "".join(cur).strip()
    if tail:
        args.append(tail)
    return args


def lint_bas_file(path: Path) -> List[str]:
    """Run all static checks against a single .bas file and return error strings."""
    errors: List[str] = []
    text = path.read_text(encoding="utf-8", errors="replace")
    raw_lines = text.splitlines()

    if not any(re.match(r"^Option\s+Explicit\b", ln.strip(), re.I) for ln in raw_lines[:80]):
        errors.append(f"{path.name}: Missing 'Option Explicit' near top of module.")

    joined_masked = join_continuations(raw_lines, mask_strings=True)

    # Pass 1: Module metadata, Type blocks, procedure signatures, and label lookup
    mod_consts: Dict[str, str] = {}
    type_defs: Dict[str, Dict[str, str]] = {}
    procs: Dict[str, Tuple[str, str, List[Tuple[str, bool, str, bool]], int]] = {}
    proc_labels: Dict[str, Set[str]] = {}

    first_proc_ln = None
    last_type_ln = None
    in_type_name: str | None = None
    in_hdr = True
    scan_proc: str | None = None

    proc_sig_re = re.compile(
        r"^(?:Public\s+|Private\s+)?(Sub|Function)\s+([A-Za-z_]\w*)\s*\((.*)\)(?:\s+As\s+([A-Za-z0-9_.]+))?\s*$",
        re.I,
    )

    for ln, stmt in joined_masked:
        if not stmt:
            continue

        m_type = re.match(r"^(?:Private\s+|Public\s+)?Type\s+([A-Za-z_]\w*)\b", stmt, re.I)
        if m_type:
            in_type_name = m_type.group(1).lower()
            type_defs[in_type_name] = {}
            last_type_ln = ln
            if in_type_name in RESERVED_KEYWORDS:
                errors.append(f"L{ln}: Type name '{m_type.group(1)}' is a reserved VBA keyword.")
            continue

        if in_type_name is not None:
            if re.match(r"^End\s+Type\b", stmt, re.I):
                in_type_name = None
            else:
                fm = re.match(r"^([A-Za-z_]\w*)\s+As\s+([A-Za-z0-9_.]+)", stmt, re.I)
                if fm:
                    fld, ftype = fm.group(1), fm.group(2)
                    if fld.lower() in RESERVED_KEYWORDS:
                        errors.append(f"L{ln}: Type field '{fld}' is a reserved VBA keyword.")
                    type_defs[in_type_name][fld.lower()] = ftype
            continue

        m_proc = proc_sig_re.match(stmt)
        if m_proc and not stmt.lower().startswith("declare "):
            in_hdr = False
            kind, pname, params_raw, ret_type = m_proc.groups()
            if first_proc_ln is None:
                first_proc_ln = ln
            if pname.lower() in RESERVED_KEYWORDS:
                errors.append(f"L{ln}: Procedure name '{pname}' is a reserved VBA keyword.")
            params = parse_params(params_raw or "")
            for par_name, _, _, _ in params:
                if par_name.lower() in RESERVED_KEYWORDS:
                    errors.append(
                        f"L{ln} ({pname}): Parameter '{par_name}' is a reserved VBA keyword."
                    )
            procs[pname.lower()] = (kind.lower(), pname, params, ln)
            scan_proc = pname.lower()
            proc_labels[scan_proc] = set()
            continue

        if scan_proc and re.match(r"^End\s+(Sub|Function)\b", stmt, re.I):
            scan_proc = None
            continue

        if scan_proc and re.fullmatch(r"[A-Za-z_]\w*:", stmt):
            proc_labels[scan_proc].add(stmt[:-1].lower())
            continue

        if in_hdr:
            cm = re.match(r"^(?:Private\s+|Public\s+)?Const\s+([A-Za-z_]\w*)\b", stmt, re.I)
            if cm:
                cname = cm.group(1)
                if cname.lower() in RESERVED_KEYWORDS:
                    errors.append(f"L{ln}: Module Const '{cname}' is a reserved VBA keyword.")
                mod_consts[cname.lower()] = "Variant"

    if last_type_ln is not None and first_proc_ln is not None and last_type_ln > first_proc_ln:
        errors.append(f"L{last_type_ln}: Type block appears after first procedure (L{first_proc_ln}).")

    # Pass 2: Procedure body checks (nesting, keywords, Dims, Option Explicit, ByRef calls)
    cur_kind: str | None = None
    cur_proc: str | None = None
    dims_seen: Dict[str, int] = {}
    locals_map: Dict[str, str] = {}
    block_stack: List[Tuple[str, int]] = []

    for ln, stmt in joined_masked:
        if not stmt:
            continue

        m_proc = proc_sig_re.match(stmt)
        if m_proc and cur_proc is None and not stmt.lower().startswith("declare "):
            cur_kind, cur_proc, params, _ = procs[m_proc.group(2).lower()]
            dims_seen = {p_name.lower(): ln for p_name, _, _, _ in params}
            locals_map = {p_name.lower(): p_type for p_name, _, p_type, _ in params}
            locals_map[cur_proc.lower()] = "Variant"
            block_stack = []
            continue

        if cur_proc is None:
            continue

        for part in split_statements(stmt):
            m_end = re.match(r"^End\s+(Sub|Function)\b", part, re.I)
            if m_end:
                end_kind = m_end.group(1).lower()
                if end_kind != cur_kind:
                    errors.append(
                        f"L{ln} ({cur_proc}): 'End {end_kind}' mismatches '{cur_kind}'."
                    )
                if block_stack:
                    errors.append(
                        f"L{ln} ({cur_proc}): Unclosed block(s) before End {end_kind}: {block_stack}."
                    )
                cur_proc = None
                cur_kind = None
                break

            if re.fullmatch(r"[A-Za-z_]\w*:", part):
                continue

            # Control-flow nesting
            part_l = part.lower()
            if re.match(r"^if\b", part_l):
                tm = re.search(r"\bthen\b(.*)$", part_l)
                if tm and tm.group(1).strip() == "":
                    block_stack.append(("If", ln))
            elif re.match(r"^elseif\b", part_l) or part_l == "else":
                if not block_stack or block_stack[-1][0] != "If":
                    errors.append(f"L{ln} ({cur_proc}): Orphan '{part}' outside If block.")
            elif re.match(r"^end\s+if\b", part_l):
                if not block_stack or block_stack[-1][0] != "If":
                    errors.append(f"L{ln} ({cur_proc}): Stray 'End If'.")
                else:
                    block_stack.pop()
            elif re.match(r"^for\b", part_l):
                block_stack.append(("For", ln))
            elif re.match(r"^next\b", part_l):
                if not block_stack or block_stack[-1][0] != "For":
                    errors.append(f"L{ln} ({cur_proc}): Stray 'Next'.")
                else:
                    block_stack.pop()
            elif re.match(r"^do\b", part_l):
                block_stack.append(("Do", ln))
            elif re.match(r"^loop\b", part_l):
                if not block_stack or block_stack[-1][0] != "Do":
                    errors.append(f"L{ln} ({cur_proc}): Stray 'Loop'.")
                else:
                    block_stack.pop()
            elif re.match(r"^select\s+case\b", part_l):
                block_stack.append(("Select", ln))
            elif re.match(r"^end\s+select\b", part_l):
                if not block_stack or block_stack[-1][0] != "Select":
                    errors.append(f"L{ln} ({cur_proc}): Stray 'End Select'.")
                else:
                    block_stack.pop()
            elif re.match(r"^with\b", part_l):
                block_stack.append(("With", ln))
            elif re.match(r"^end\s+with\b", part_l):
                if not block_stack or block_stack[-1][0] != "With":
                    errors.append(f"L{ln} ({cur_proc}): Stray 'End With'.")
                else:
                    block_stack.pop()

            # Dim / Const declarations + duplicate Dim + reserved keyword check
            m_decl = re.match(r"^(Dim|Const)\s+(.+)$", part, re.I)
            if m_decl:
                decl_kind, decl_body = m_decl.group(1).lower(), m_decl.group(2)
                for seg in decl_body.split(","):
                    seg = seg.strip()
                    vm = re.match(
                        r"^([A-Za-z_]\w*)(\(\s*\))?(?:\s+As\s+([A-Za-z0-9_.]+))?",
                        seg,
                        re.I,
                    )
                    if not vm:
                        continue
                    vname = vm.group(1)
                    is_arr = bool(vm.group(2))
                    vtype = (vm.group(3) or "Variant") + ("()" if is_arr else "")
                    vl = vname.lower()
                    if vl in RESERVED_KEYWORDS:
                        errors.append(
                            f"L{ln} ({cur_proc}): Declared identifier '{vname}' is a reserved VBA keyword."
                        )
                    if decl_kind == "dim":
                        if vl in dims_seen:
                            errors.append(
                                f"L{ln} ({cur_proc}): Duplicate Dim '{vname}' (previously L{dims_seen[vl]})."
                            )
                        dims_seen[vl] = ln
                    locals_map[vl] = vtype
                continue

            # Option Explicit identifier check
            clean_expr = re.sub(r"\.[A-Za-z_]\w*", "", part)
            clean_expr = re.sub(r"&H[0-9A-Fa-f]+", "", clean_expr)
            clean_expr = re.sub(r"\b\d+(?:\.\d+)?(?:E[+-]?\d+)?[#@!&%]?", "", clean_expr, flags=re.I)
            clean_expr = re.sub(r'"[^"]*"', "", clean_expr)
            for tm in re.finditer(r"\b([A-Za-z_]\w*)\b", clean_expr):
                tok = tm.group(1)
                tl = tok.lower()
                if (
                    tl not in locals_map
                    and tl not in mod_consts
                    and tl not in procs
                    and tl not in type_defs
                    and tl not in BUILTIN_IDENTIFIERS
                    and tl not in proc_labels[cur_proc.lower()]
                ):
                    errors.append(
                        f"L{ln} ({cur_proc}): Possibly undeclared identifier '{tok}' in '{part}'."
                    )

            # Call-site validation: check Sub calls and Function calls for arg count & ByRef safety
            call_candidates: List[Tuple[str, List[str]]] = []
            m_sub = re.match(r"^(?:Call\s+)?([A-Za-z_]\w*)\s+(.+)$", part)
            if m_sub and m_sub.group(1).lower() in procs:
                cname_raw = m_sub.group(1)
                if not re.match(rf"^{re.escape(cname_raw)}\s*=", part, re.I):
                    call_candidates.append((cname_raw.lower(), split_top_level_args(m_sub.group(2))))

            for fm in re.finditer(r"\b([A-Za-z_]\w*)\s*\(", part):
                fn = fm.group(1).lower()
                if fn in procs and fn not in locals_map:
                    pos = fm.end()
                    depth = 1
                    inner: List[str] = []
                    while pos < len(part) and depth > 0:
                        if part[pos] == "(":
                            depth += 1
                        elif part[pos] == ")":
                            depth -= 1
                            if depth == 0:
                                break
                        inner.append(part[pos])
                        pos += 1
                    call_candidates.append((fn, split_top_level_args("".join(inner))))

            for fn_key, args in call_candidates:
                _, cname, plist, _ = procs[fn_key]
                min_args = sum(1 for _, _, _, opt in plist if not opt)
                if not (min_args <= len(args) <= len(plist)):
                    errors.append(
                        f"L{ln} ({cur_proc}): Call to {cname} has {len(args)} arg(s), expected {min_args}..{len(plist)}."
                    )
                    continue
                for (par_name, is_byval, par_type, _), arg in zip(plist, args):
                    if is_byval:
                        continue
                    pt_l = par_type.lower()
                    if pt_l == "variant":
                        continue
                    # Flag Variant/untyped expressions or mismatched locals passed to typed ByRef params
                    if re.fullmatch(r"[A-Za-z_]\w*", arg):
                        arg_t = locals_map.get(arg.lower(), mod_consts.get(arg.lower(), ""))
                        if arg_t and arg_t.lower() != pt_l and not (pt_l == "object" and "." in arg_t):
                            errors.append(
                                f"L{ln} ({cur_proc}): Argument '{arg}' ({arg_t}) passed to ByRef {par_name} As {par_type} in {cname}."
                            )
                    elif arg.lower() == "nothing" or arg.startswith('"') or re.fullmatch(r"[+-]?\d+(?:\.\d+)?#?", arg):
                        continue
                    elif pt_l.startswith("sldworks.") or pt_l in ("collection", "object"):
                        errors.append(
                            f"L{ln} ({cur_proc}): Untyped/Variant expression '{arg}' passed to ByRef {par_name} As {par_type} in {cname}."
                        )

    if cur_proc is not None:
        errors.append(f"EOF: Unclosed procedure '{cur_proc}'.")

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description="Static VBA pre-flight linter for .bas files")
    parser.add_argument("files", nargs="*", help="Path(s) to .bas files (defaults to all *.bas in repo root)")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent
    targets = [Path(f) for f in args.files] if args.files else sorted(repo_root.glob("*.bas"))
    if not targets:
        print("No .bas files found.", file=sys.stderr)
        return 1

    total_errors = 0
    for target in targets:
        errs = lint_bas_file(target)
        if errs:
            total_errors += len(errs)
            for e in errs:
                print(f"[FAIL] {target.name}: {e}", file=sys.stderr)
        else:
            print(f"[PASS] VBA static lint passed: {target.name}")

    return 1 if total_errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
