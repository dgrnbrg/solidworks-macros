using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorksJoineryAddin.Data;
using SolidWorksJoineryAddin.MacroFeatures;

namespace SolidWorksJoineryAddin.UI
{
    public class CalloutManager
    {
        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _model;
        private readonly Dictionary<string, ICallout> _activeCallouts = new Dictionary<string, ICallout>();

        public CalloutManager(ISldWorks swApp, IModelDoc2 model)
        {
            _swApp = swApp;
            _model = model;
        }

        /// <summary>
        /// Updates or creates a 3D viewport callout anchored at the midpoint of the joint.
        /// </summary>
        public void UpdateCallout(JointDefinition joint)
        {
            if (_model == null || joint == null) return;

            Vector3D anchor = joint.Origin + joint.SpanUnit * (0.5 * joint.SpanLength);
            UpdateCallout(joint, anchor.X, anchor.Y, anchor.Z);
        }

        /// <summary>
        /// Updates or creates a 3D viewport callout for a joint definition at (x, y, z).
        /// </summary>
        public void UpdateCallout(JointDefinition joint, double x, double y, double z)
        {
            if (_model == null || joint == null) return;

            try
            {
                IModelView view = (IModelView)_model.ActiveView;
                if (view == null) return;

                ICallout callout;
                if (!_activeCallouts.TryGetValue(joint.JointId, out callout))
                {
                    callout = (ICallout)view.CreateCallout(4, null);
                    if (callout == null) return;
                    _activeCallouts[joint.JointId] = callout;
                }

                // Row 0: Joint Tag & Type
                callout.set_Label2(0, "Joint");
                callout.set_Value(0, string.Format("{0} ({1})", joint.JointId, joint.Type));

                // Row 1: Bit Radius & Clearance
                callout.set_Label2(1, "Tool / Fit");
                callout.set_Value(1, string.Format("R={0:F3}\" | Fit={1:+0.000;-0.000;0.000}\"",
                    joint.ToolBitRadius * 39.3701, joint.SignedClearance * 39.3701));

                // Row 2: Dogbone status
                callout.set_Label2(2, "Dogbone");
                callout.set_Value(2, joint.EnableDogbones ? joint.ReliefType.ToString() : "Disabled");

                // Row 3: Keying / Poka-Yoke Status
                callout.set_Label2(3, "Keying");
                if (joint.EnableKeying && joint.IsKeyed)
                {
                    callout.set_Value(3, string.Format("Tab {0} [{1}]", joint.KeyedTabIndex + 1,
                        joint.KeyDirection == KeyingDirection.Positive ? "+0.5\" Dir" : "-0.5\" Dir"));
                }
                else if (joint.HasConflict)
                {
                    callout.set_Value(3, "⚠️ Conflict Detected");
                }
                else
                {
                    callout.set_Value(3, "Unique / Unkeyed");
                }

                callout.SetTargetPoint(0, x, y, z);
                callout.Display(true);
            }
            catch { }
        }

        /// <summary>
        /// Hides and removes all active callouts from the viewport.
        /// </summary>
        public void ClearCallouts()
        {
            try
            {
                foreach (ICallout callout in _activeCallouts.Values)
                {
                    try { callout?.Display(false); } catch { }
                }
                _activeCallouts.Clear();
            }
            catch { }
        }
    }
}
