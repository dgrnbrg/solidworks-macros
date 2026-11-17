using System;
using SolidWorks.Interop.sldworks;
using SolidWorks3DPrintDFMAddin.Data;

namespace SolidWorks3DPrintDFMAddin.UI
{
    public class DfmCalloutManager
    {
        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _model;
        private ICallout _statusCallout;

        public DfmCalloutManager(ISldWorks swApp, IModelDoc2 model)
        {
            _swApp = swApp;
            _model = model;
        }

        public void UpdateBedAndSummaryCallout(DfmConfig config, int holeCount, int recessCount)
        {
            if (_model == null || config == null)
                return;

            try
            {
                IModelView view = (IModelView)_model.ActiveView;
                if (view == null) return;

                if (_statusCallout == null)
                {
                    _statusCallout = (ICallout)view.CreateCallout(3, null);
                    if (_statusCallout == null) return;
                }

                _statusCallout.set_Label2(0, "Print +Z Up");
                _statusCallout.set_Value(0, string.Format("({0:+0.00;-0.00;0.00}, {1:+0.00;-0.00;0.00}, {2:+0.00;-0.00;0.00})",
                    config.BedUpVector.X, config.BedUpVector.Y, config.BedUpVector.Z));

                _statusCallout.set_Label2(1, "Teardrops");
                _statusCallout.set_Value(1, string.Format("{0} holes ({1:F0}° slope, {2:F2}mm cap)",
                    holeCount, config.OverhangAngleFromBedDeg, config.MaxCapHeightMm));

                _statusCallout.set_Label2(2, "Bridges");
                _statusCallout.set_Value(2, string.Format("{0} recesses (2x {1:F2}mm layers)",
                    recessCount, config.LayerHeightMm));

                _statusCallout.SetTargetPoint(0,
                    config.BedReferencePoint.X,
                    config.BedReferencePoint.Y,
                    config.BedReferencePoint.Z);

                _statusCallout.Display(true);
            }
            catch { }
        }

        public void ClearCallouts()
        {
            try
            {
                if (_statusCallout != null)
                {
                    _statusCallout.Display(false);
                    _statusCallout = null;
                }
            }
            catch { }
        }
    }
}
