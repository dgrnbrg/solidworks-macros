using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SolidWorks3DPrintDFMAddin.Builders;
using SolidWorks3DPrintDFMAddin.Core;
using SolidWorks3DPrintDFMAddin.Data;

namespace SolidWorks3DPrintDFMAddin.UI
{
    public enum DfmCommandMode
    {
        TeardropFillet = 0,
        CrisscrossBridge = 1
    }

    [ComVisible(true)]
    [Guid("7F2E91C4-8B4A-4D53-9A2E-1B6D4C8E7019")]
    public class DfmPMPHandler : IPropertyManagerPage2Handler9
    {
        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _model;
        private readonly IFeature _existingMacroFeature;
        private readonly DfmConfig _config;
        private readonly DfmCommandMode _mode;
        private readonly DfmCalloutManager _calloutMgr;

        private IPropertyManagerPage2 _page;

        private IPropertyManagerPageGroup _grpBed;
        private IPropertyManagerPageGroup _grpTeardrops;
        private IPropertyManagerPageGroup _grpBridges;

        private const int MARK_BED_REF = 1;
        private const int MARK_BODIES = 2;
        private const int MARK_TEARDROP_FACES = 4;
        private const int MARK_BRIDGE_FACES = 8;

        private const int ID_GRP_BED = 100;
        private const int ID_LBL_BED = 101;
        private const int ID_SEL_BED = 102;
        private const int ID_CHK_FLIP_BED = 103;
        private const int ID_LBL_BODIES = 104;
        private const int ID_SEL_BODIES = 105;
        private const int ID_LBL_STATUS = 106;

        private const int ID_GRP_TEARDROPS = 200;
        private const int ID_LBL_TEARDROP_FACES = 201;
        private const int ID_SEL_TEARDROP_FACES = 202;
        private const int ID_BTN_AUTO_TEARDROP = 203;
        private const int ID_LBL_OVERHANG_ANGLE = 204;
        private const int ID_NUM_OVERHANG_ANGLE = 205;
        private const int ID_CHK_CAP_HEIGHT = 206;
        private const int ID_LBL_MAX_CAP_HEIGHT = 207;
        private const int ID_NUM_MAX_CAP_HEIGHT = 208;
        private const int ID_LBL_MIN_CIRCLE_ANGLE = 209;
        private const int ID_NUM_MIN_CIRCLE_ANGLE = 210;

        private const int ID_GRP_BRIDGES = 300;
        private const int ID_LBL_BRIDGE_FACES = 301;
        private const int ID_SEL_BRIDGE_FACES = 302;
        private const int ID_BTN_AUTO_BRIDGES = 303;
        private const int ID_LBL_LAYER_HEIGHT = 304;
        private const int ID_NUM_LAYER_HEIGHT = 305;
        private const int ID_LBL_BRIDGE_ANGLE = 306;
        private const int ID_NUM_BRIDGE_ANGLE = 307;

        private IPropertyManagerPageSelectionbox _selBedRef;
        private IPropertyManagerPageCheckbox _chkFlipBed;
        private IPropertyManagerPageSelectionbox _selBodies;
        private IPropertyManagerPageLabel _lblStatus;

        private IPropertyManagerPageSelectionbox _selTeardropFaces;
        private IPropertyManagerPageButton _btnAutoSelectTeardrops;
        private IPropertyManagerPageNumberbox _numOverhangAngle;
        private IPropertyManagerPageCheckbox _chkCapHeight;
        private IPropertyManagerPageNumberbox _numMaxCapHeight;
        private IPropertyManagerPageNumberbox _numMinCircleAngle;

        private IPropertyManagerPageSelectionbox _selBridgeFaces;
        private IPropertyManagerPageButton _btnAutoSelectBridges;
        private IPropertyManagerPageNumberbox _numLayerHeight;
        private IPropertyManagerPageNumberbox _numBridgeAngle;

        private object _currentBedRefEntity;
        private bool _isAutoPopulating;
        private bool _wasAccepted;
        private List<DetectedHoleFaceGroup> _pendingHoleGroups = new List<DetectedHoleFaceGroup>();
        private List<DetectedRecessFace> _pendingRecesses = new List<DetectedRecessFace>();

        public DfmPMPHandler(
            ISldWorks swApp,
            IModelDoc2 model,
            IFeature existingMacroFeature,
            DfmConfig config,
            DfmCommandMode mode)
        {
            _swApp = swApp;
            _model = model;
            _existingMacroFeature = existingMacroFeature;
            _config = config ?? DfmConfig.LoadDefaults();
            _mode = mode;
            _calloutMgr = new DfmCalloutManager(swApp, model);
        }

        public void Show()
        {
            try
            {
                if (_swApp == null) return;

                _model?.ClearSelection2(true);

                int errors = 0;
                int options = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                                    swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                                    swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage);

                string pageTitle = _mode == DfmCommandMode.TeardropFillet
                    ? "Teardrop Fillet (3D Print DFM)"
                    : "Crisscross Bridge (3D Print DFM)";

                _page = (IPropertyManagerPage2)_swApp.CreatePropertyManagerPage(pageTitle, options, this, ref errors);
                if (_page == null || errors != 0)
                {
                    SwAddin.Log("CreatePropertyManagerPage failed with error code: " + errors);
                    return;
                }

                BuildUI();
                int showRes = _page.Show();
                SwAddin.Log(string.Format("PropertyManagerPage.Show({0}, isEdit={1}) returned: {2}",
                    pageTitle, _existingMacroFeature != null, showRes));
            }
            catch (Exception ex)
            {
                SwAddin.Log("DfmPMPHandler.Show() error: " + ex);
            }
        }

        private static double Clamp(double val, double min, double max)
        {
            if (double.IsNaN(val) || double.IsInfinity(val)) return min;
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        private void BuildUI()
        {
            int grpOpts = (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible |
                                swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded);

            int ctrlOpts = (int)(swAddControlOptions_e.swControlOptions_Visible |
                                 swAddControlOptions_e.swControlOptions_Enabled);

            short alignLeft = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge;

            // 1. Print Bed Reference & Bodies Group
            _grpBed = (IPropertyManagerPageGroup)_page.AddGroupBox(ID_GRP_BED, "Print Bed & Target Bodies", grpOpts);

            _grpBed.AddControl2(
                ID_LBL_BED,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Print Bed Reference (Face / Plane / Edge):", alignLeft, ctrlOpts, "");

            _selBedRef = (IPropertyManagerPageSelectionbox)_grpBed.AddControl2(
                ID_SEL_BED,
                (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                "Print Bed Reference", alignLeft, ctrlOpts,
                "Select a planar face resting on the print bed, a reference plane, or a vertical edge.");
            if (_selBedRef != null)
            {
                _selBedRef.SetSelectionFilters(new int[]
                {
                    (int)swSelectType_e.swSelFACES,
                    (int)swSelectType_e.swSelDATUMPLANES,
                    (int)swSelectType_e.swSelEDGES
                });
                _selBedRef.SingleEntityOnly = true;
                _selBedRef.Mark = MARK_BED_REF;
                _selBedRef.Height = 28;
            }

            _chkFlipBed = (IPropertyManagerPageCheckbox)_grpBed.AddControl2(
                ID_CHK_FLIP_BED,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Flip Print Up (+Z) Direction", alignLeft, ctrlOpts,
                "Reverses the Print Bed Up (+Z) direction vector.");
            if (_chkFlipBed != null)
                _chkFlipBed.Checked = _config.FlipBedDirection;

            _grpBed.AddControl2(
                ID_LBL_BODIES,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Target Solid Bodies (Leave empty for All):", alignLeft, ctrlOpts, "");

            _selBodies = (IPropertyManagerPageSelectionbox)_grpBed.AddControl2(
                ID_SEL_BODIES,
                (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                "Target Solid Bodies", alignLeft, ctrlOpts,
                "Select target solid bodies, or leave empty to process all solid bodies in the part.");
            if (_selBodies != null)
            {
                _selBodies.SetSelectionFilters(new int[] { (int)swSelectType_e.swSelSOLIDBODIES });
                _selBodies.SingleEntityOnly = false;
                _selBodies.Mark = MARK_BODIES;
                _selBodies.Height = 32;
            }

            _lblStatus = (IPropertyManagerPageLabel)_grpBed.AddControl2(
                ID_LBL_STATUS,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Select a Print Bed face to auto-detect qualifying geometry.", alignLeft, ctrlOpts,
                "Status summary");

            // 2A. Teardrop Fillet Controls
            if (_mode == DfmCommandMode.TeardropFillet)
            {
                _grpTeardrops = (IPropertyManagerPageGroup)_page.AddGroupBox(
                    ID_GRP_TEARDROPS, "Teardrop Fillet Parameters", grpOpts);

                _grpTeardrops.AddControl2(
                    ID_LBL_TEARDROP_FACES,
                    (short)swPropertyManagerPageControlType_e.swControlType_Label,
                    "Circular Hole Faces (40°–90° to Bed):", alignLeft, ctrlOpts, "");

                _selTeardropFaces = (IPropertyManagerPageSelectionbox)_grpTeardrops.AddControl2(
                    ID_SEL_TEARDROP_FACES,
                    (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                    "Circular Hole Faces", alignLeft, ctrlOpts,
                    "Auto-populated when Bed Reference is set; click to add or remove circular hole faces.");
                if (_selTeardropFaces != null)
                {
                    _selTeardropFaces.SetSelectionFilters(new int[] { (int)swSelectType_e.swSelFACES });
                    _selTeardropFaces.SingleEntityOnly = false;
                    _selTeardropFaces.Mark = MARK_TEARDROP_FACES;
                    _selTeardropFaces.Height = 65;
                }

                _btnAutoSelectTeardrops = (IPropertyManagerPageButton)_grpTeardrops.AddControl2(
                    ID_BTN_AUTO_TEARDROP,
                    (short)swPropertyManagerPageControlType_e.swControlType_Button,
                    "Auto-Select Qualifying Holes", alignLeft, ctrlOpts,
                    "Re-scan target bodies for circular holes angled between Min Circle Angle and 90° to the bed.");

                _grpTeardrops.AddControl2(
                    ID_LBL_OVERHANG_ANGLE,
                    (short)swPropertyManagerPageControlType_e.swControlType_Label,
                    "Overhang Angle from Bed (deg):", alignLeft, ctrlOpts, "");

                _numOverhangAngle = (IPropertyManagerPageNumberbox)_grpTeardrops.AddControl2(
                    ID_NUM_OVERHANG_ANGLE,
                    (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                    "Overhang Angle from Bed (deg)", alignLeft, ctrlOpts,
                    "Teardrop roof slope measured from horizontal print bed (default 40° = 50° from vertical).");
                if (_numOverhangAngle != null)
                {
                    _numOverhangAngle.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, 15.0, 75.0, 5.0, true);
                    _numOverhangAngle.Value = Clamp(_config.OverhangAngleFromBedDeg, 15.0, 75.0);
                }

                _chkCapHeight = (IPropertyManagerPageCheckbox)_grpTeardrops.AddControl2(
                    ID_CHK_CAP_HEIGHT,
                    (short)swPropertyManagerPageControlType_e.swControlType_Checkbox,
                    "Cap Max Height (Flat-Top Truncation)", alignLeft, ctrlOpts,
                    "Truncate teardrop peak to a maximum vertical extension along the print bed normal.");
                if (_chkCapHeight != null)
                    _chkCapHeight.Checked = _config.EnableMaxHeightCap;

                _grpTeardrops.AddControl2(
                    ID_LBL_MAX_CAP_HEIGHT,
                    (short)swPropertyManagerPageControlType_e.swControlType_Label,
                    "Max Cap Height along Bed Normal (mm):", alignLeft, ctrlOpts, "");

                _numMaxCapHeight = (IPropertyManagerPageNumberbox)_grpTeardrops.AddControl2(
                    ID_NUM_MAX_CAP_HEIGHT,
                    (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                    "Max Cap Height along Bed Normal (mm)", alignLeft, ctrlOpts,
                    "Maximum vertical height above the nominal top of the circle measured normal to the print bed (default 0.6 mm).");
                if (_numMaxCapHeight != null)
                {
                    _numMaxCapHeight.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, 0.05, 5.0, 0.1, true);
                    _numMaxCapHeight.Value = Clamp(_config.MaxCapHeightMm, 0.05, 5.0);
                    ((IPropertyManagerPageControl)_numMaxCapHeight).Enabled = _config.EnableMaxHeightCap;
                }

                _grpTeardrops.AddControl2(
                    ID_LBL_MIN_CIRCLE_ANGLE,
                    (short)swPropertyManagerPageControlType_e.swControlType_Label,
                    "Min Circle Angle to Bed (deg):", alignLeft, ctrlOpts, "");

                _numMinCircleAngle = (IPropertyManagerPageNumberbox)_grpTeardrops.AddControl2(
                    ID_NUM_MIN_CIRCLE_ANGLE,
                    (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                    "Min Circle Angle to Bed (deg)", alignLeft, ctrlOpts,
                    "Minimum circle plane angle to the bed (default 40°, up to 90° vertical circles).");
                if (_numMinCircleAngle != null)
                {
                    _numMinCircleAngle.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, 10.0, 90.0, 5.0, true);
                    _numMinCircleAngle.Value = Clamp(_config.MinCircleAngleToBedDeg, 10.0, 90.0);
                }
            }

            // 2B. Crisscross Bridge Controls
            if (_mode == DfmCommandMode.CrisscrossBridge)
            {
                _grpBridges = (IPropertyManagerPageGroup)_page.AddGroupBox(
                    ID_GRP_BRIDGES, "Integral Crisscross Bridge Parameters", grpOpts);

                _grpBridges.AddControl2(
                    ID_LBL_BRIDGE_FACES,
                    (short)swPropertyManagerPageControlType_e.swControlType_Label,
                    "Downward Recessed Faces:", alignLeft, ctrlOpts, "");

                _selBridgeFaces = (IPropertyManagerPageSelectionbox)_grpBridges.AddControl2(
                    ID_SEL_BRIDGE_FACES,
                    (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                    "Downward Recessed Faces", alignLeft, ctrlOpts,
                    "Auto-populated when Bed Reference is set; click to add or remove recessed faces.");
                if (_selBridgeFaces != null)
                {
                    _selBridgeFaces.SetSelectionFilters(new int[] { (int)swSelectType_e.swSelFACES });
                    _selBridgeFaces.SingleEntityOnly = false;
                    _selBridgeFaces.Mark = MARK_BRIDGE_FACES;
                    _selBridgeFaces.Height = 65;
                }

                _btnAutoSelectBridges = (IPropertyManagerPageButton)_grpBridges.AddControl2(
                    ID_BTN_AUTO_BRIDGES,
                    (short)swPropertyManagerPageControlType_e.swControlType_Button,
                    "Auto-Select Downward Recesses", alignLeft, ctrlOpts,
                    "Re-scan target bodies for downward-facing recessed faces containing an inner hole loop.");

                _grpBridges.AddControl2(
                    ID_LBL_LAYER_HEIGHT,
                    (short)swPropertyManagerPageControlType_e.swControlType_Label,
                    "Slicer Layer Height (mm):", alignLeft, ctrlOpts, "");

                _numLayerHeight = (IPropertyManagerPageNumberbox)_grpBridges.AddControl2(
                    ID_NUM_LAYER_HEIGHT,
                    (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                    "Slicer Layer Height (mm)", alignLeft, ctrlOpts,
                    "Single layer height for Tier 1 (1x) and Tier 2 (2x) integral bridge cuts (default 0.20 mm).");
                if (_numLayerHeight != null)
                {
                    _numLayerHeight.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, 0.04, 1.0, 0.04, true);
                    _numLayerHeight.Value = Clamp(_config.LayerHeightMm, 0.04, 1.0);
                }

                _grpBridges.AddControl2(
                    ID_LBL_BRIDGE_ANGLE,
                    (short)swPropertyManagerPageControlType_e.swControlType_Label,
                    "Bridge Rotation Angle (deg):", alignLeft, ctrlOpts, "");

                _numBridgeAngle = (IPropertyManagerPageNumberbox)_grpBridges.AddControl2(
                    ID_NUM_BRIDGE_ANGLE,
                    (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                    "Bridge Rotation Angle (deg)", alignLeft, ctrlOpts,
                    "In-plane rotation angle for the primary Tier 1 bridge chords.");
                if (_numBridgeAngle != null)
                {
                    _numBridgeAngle.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, -90.0, 90.0, 15.0, true);
                    _numBridgeAngle.Value = Clamp(_config.BridgeAngleOffsetDeg, -90.0, 90.0);
                }
            }
        }

        private void SyncSettingsFromControls()
        {
            try
            {
                if (_chkFlipBed != null) _config.FlipBedDirection = _chkFlipBed.Checked;
                if (_numOverhangAngle != null) _config.OverhangAngleFromBedDeg = _numOverhangAngle.Value;
                if (_chkCapHeight != null) _config.EnableMaxHeightCap = _chkCapHeight.Checked;
                if (_numMaxCapHeight != null) _config.MaxCapHeightMm = _numMaxCapHeight.Value;
                if (_numMinCircleAngle != null) _config.MinCircleAngleToBedDeg = _numMinCircleAngle.Value;

                if (_numLayerHeight != null) _config.LayerHeightMm = _numLayerHeight.Value;
                if (_numBridgeAngle != null) _config.BridgeAngleOffsetDeg = _numBridgeAngle.Value;
            }
            catch (Exception ex)
            {
                SwAddin.Log("SyncSettingsFromControls error: " + ex);
            }
        }

        private void RefreshBedAndAutoSelect()
        {
            if (_model == null || _isAutoPopulating) return;

            try
            {
                SyncSettingsFromControls();

                ISelectionMgr selMgr = (ISelectionMgr)_model.SelectionManager;
                object bedEnt = GetFirstSelectedEntityByMark(selMgr, MARK_BED_REF) ?? _currentBedRefEntity;
                if (bedEnt != null)
                {
                    _currentBedRefEntity = bedEnt;
                    if (TopologyScanner.TryResolveBedReference(
                        bedEnt,
                        _config.FlipBedDirection,
                        out Vector3D bedUp,
                        out Vector3D refPt))
                    {
                        _config.BedUpVector = bedUp;
                        _config.BedReferencePoint = refPt;
                    }
                }

                List<IBody2> bodies = GetCurrentlySelectedBodies(selMgr);

                _isAutoPopulating = true;
                try
                {
                    SelectData selData = (SelectData)selMgr.CreateSelectData();

                    if (_mode == DfmCommandMode.TeardropFillet)
                    {
                        ClearSelectionByMark(selMgr, MARK_TEARDROP_FACES);
                        List<DetectedHoleFaceGroup> holes = TopologyScanner.ScanQualifyingTeardropHoles(bodies, _config);
                        selData.Mark = MARK_TEARDROP_FACES;
                        foreach (DetectedHoleFaceGroup g in holes)
                        {
                            if (g.Faces.Count > 0 && g.Faces[0] is IEntity ent)
                            {
                                ent.Select4(true, selData);
                            }
                        }
                    }
                    else if (_mode == DfmCommandMode.CrisscrossBridge)
                    {
                        ClearSelectionByMark(selMgr, MARK_BRIDGE_FACES);
                        List<DetectedRecessFace> recesses = TopologyScanner.ScanQualifyingDownwardRecesses(bodies, _config);
                        selData.Mark = MARK_BRIDGE_FACES;
                        foreach (DetectedRecessFace r in recesses)
                        {
                            if (r.Face is IEntity ent)
                            {
                                ent.Select4(true, selData);
                            }
                        }
                    }

                    UpdateStatusAndCallout(selMgr);
                }
                finally
                {
                    _isAutoPopulating = false;
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("RefreshBedAndAutoSelect error: " + ex);
            }
        }

        private void UpdateStatusAndCallout(ISelectionMgr selMgr)
        {
            try
            {
                int holeFaceCount = GetSelectedEntitiesByMark<IFace2>(selMgr, MARK_TEARDROP_FACES).Count;
                int recessFaceCount = GetSelectedEntitiesByMark<IFace2>(selMgr, MARK_BRIDGE_FACES).Count;

                if (_lblStatus != null)
                {
                    if (_mode == DfmCommandMode.TeardropFillet)
                    {
                        _lblStatus.Caption = string.Format(
                            "Bed Up: ({0:F2},{1:F2},{2:F2}) | {3} Qualifying Circular Holes",
                            _config.BedUpVector.X, _config.BedUpVector.Y, _config.BedUpVector.Z, holeFaceCount);
                    }
                    else
                    {
                        _lblStatus.Caption = string.Format(
                            "Bed Up: ({0:F2},{1:F2},{2:F2}) | {3} Downward Recesses",
                            _config.BedUpVector.X, _config.BedUpVector.Y, _config.BedUpVector.Z, recessFaceCount);
                    }
                }

                _calloutMgr.UpdateBedAndSummaryCallout(_config, holeFaceCount, recessFaceCount);
            }
            catch { }
        }

        private List<IBody2> GetCurrentlySelectedBodies(ISelectionMgr selMgr)
        {
            List<IBody2> explicitBodies = GetSelectedEntitiesByMark<IBody2>(selMgr, MARK_BODIES);
            return TopologyScanner.GetTargetSolidBodies(_model, explicitBodies);
        }

        private static object GetFirstSelectedEntityByMark(ISelectionMgr selMgr, int mark)
        {
            if (selMgr == null) return null;
            int count = selMgr.GetSelectedObjectCount2(mark);
            if (count <= 0) return null;
            return selMgr.GetSelectedObject6(1, mark);
        }

        private static List<T> GetSelectedEntitiesByMark<T>(ISelectionMgr selMgr, int mark) where T : class
        {
            List<T> list = new List<T>();
            if (selMgr == null) return list;

            int count = selMgr.GetSelectedObjectCount2(mark);
            for (int i = 1; i <= count; i++)
            {
                if (selMgr.GetSelectedObject6(i, mark) is T item && !list.Contains(item))
                {
                    list.Add(item);
                }
            }
            return list;
        }

        private static void ClearSelectionByMark(ISelectionMgr selMgr, int mark)
        {
            if (selMgr == null) return;
            int count = selMgr.GetSelectedObjectCount2(mark);
            for (int i = count; i >= 1; i--)
            {
                object obj = selMgr.GetSelectedObject6(i, mark);
                if (obj is IEntity ent)
                {
                    ent.DeSelect();
                }
            }
        }

        #region IPropertyManagerPage2Handler9 Implementation

        public void AfterActivation()
        {
            if (_model == null) return;

            ISelectionMgr selMgr = (ISelectionMgr)_model.SelectionManager;
            _isAutoPopulating = true;
            try
            {
                if (_existingMacroFeature != null)
                {
                    // Restore exact saved Bed, Body, and Hole/Recess selections when editing an existing feature
                    SelectData selData = (SelectData)selMgr.CreateSelectData();

                    if (_config.BedRefPersistId != null)
                    {
                        object bedObj = BedStateStore.ResolvePersistReference(_model, _config.BedRefPersistId);
                        if (bedObj != null)
                        {
                            _currentBedRefEntity = bedObj;
                            selData.Mark = MARK_BED_REF;
                            SelectObjectWithMark(bedObj, selData);
                        }
                    }

                    if (_config.BodyPersistIds != null)
                    {
                        selData.Mark = MARK_BODIES;
                        foreach (byte[] pid in _config.BodyPersistIds)
                        {
                            object bObj = BedStateStore.ResolvePersistReference(_model, pid);
                            if (bObj != null) SelectObjectWithMark(bObj, selData);
                        }
                    }

                    if (_mode == DfmCommandMode.TeardropFillet && _config.TeardropFacePersistIds != null)
                    {
                        selData.Mark = MARK_TEARDROP_FACES;
                        foreach (byte[] pid in _config.TeardropFacePersistIds)
                        {
                            object fObj = BedStateStore.ResolvePersistReference(_model, pid);
                            if (fObj != null) SelectObjectWithMark(fObj, selData);
                        }
                    }
                    else if (_mode == DfmCommandMode.CrisscrossBridge && _config.BridgeFacePersistIds != null)
                    {
                        selData.Mark = MARK_BRIDGE_FACES;
                        foreach (byte[] pid in _config.BridgeFacePersistIds)
                        {
                            object fObj = BedStateStore.ResolvePersistReference(_model, pid);
                            if (fObj != null) SelectObjectWithMark(fObj, selData);
                        }
                    }

                    UpdateStatusAndCallout(selMgr);
                }
                else
                {
                    // New feature: automatically restore saved per-body Print Bed Reference if already configured!
                    List<IBody2> bodies = GetCurrentlySelectedBodies(selMgr);
                    SavedBedInfo savedBed = BedStateStore.TryLoadBedForBodies(_model, bodies);
                    if (savedBed != null)
                    {
                        object bedObj = BedStateStore.ResolvePersistReference(_model, savedBed.BedRefPersistId);
                        if (bedObj != null)
                        {
                            _currentBedRefEntity = bedObj;
                            _config.BedRefPersistId = savedBed.BedRefPersistId;
                            _config.BedUpVector = savedBed.BedUpVector;
                            _config.BedReferencePoint = savedBed.BedReferencePoint;
                            _config.FlipBedDirection = savedBed.FlipBedDirection;
                            if (_chkFlipBed != null)
                                _chkFlipBed.Checked = savedBed.FlipBedDirection;

                            SelectData selData = (SelectData)selMgr.CreateSelectData();
                            selData.Mark = MARK_BED_REF;
                            SelectObjectWithMark(bedObj, selData);

                            // Unlock auto-populating flag temporarily so RefreshBedAndAutoSelect scans holes/recesses immediately
                            _isAutoPopulating = false;
                            RefreshBedAndAutoSelect();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("AfterActivation error: " + ex);
            }
            finally
            {
                _isAutoPopulating = false;
            }

            if (_selBedRef != null && _currentBedRefEntity == null)
            {
                _selBedRef.SetSelectionFocus();
            }
            else if (_mode == DfmCommandMode.TeardropFillet && _selTeardropFaces != null)
            {
                _selTeardropFaces.SetSelectionFocus();
            }
            else if (_mode == DfmCommandMode.CrisscrossBridge && _selBridgeFaces != null)
            {
                _selBridgeFaces.SetSelectionFocus();
            }
        }

        private static void SelectObjectWithMark(object obj, SelectData selData)
        {
            if (obj is IEntity ent)
            {
                ent.Select4(true, selData);
            }
            else if (obj is IFeature feat)
            {
                feat.Select2(true, selData.Mark);
            }
        }

        public void OnClose(int Reason)
        {
            try
            {
                _wasAccepted = (Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay);
                if (_wasAccepted && _model != null)
                {
                    SyncSettingsFromControls();
                    _config.SaveAsDefaults();

                    ISelectionMgr selMgr = (ISelectionMgr)_model.SelectionManager;
                    object bedEnt = GetFirstSelectedEntityByMark(selMgr, MARK_BED_REF) ?? _currentBedRefEntity;
                    if (bedEnt != null)
                    {
                        byte[] bedPid = BedStateStore.GetPersistReferenceBytes(_model, bedEnt);
                        if (bedPid != null) _config.BedRefPersistId = bedPid;

                        if (TopologyScanner.TryResolveBedReference(
                            bedEnt,
                            _config.FlipBedDirection,
                            out Vector3D bedUp,
                            out Vector3D refPt))
                        {
                            _config.BedUpVector = bedUp;
                            _config.BedReferencePoint = refPt;
                        }
                    }

                    // Persist selected bodies and save Print Bed identification per-body
                    List<IBody2> explicitBodies = GetSelectedEntitiesByMark<IBody2>(selMgr, MARK_BODIES);
                    _config.BodyPersistIds.Clear();
                    foreach (IBody2 b in explicitBodies)
                    {
                        byte[] pid = BedStateStore.GetPersistReferenceBytes(_model, b);
                        if (pid != null) _config.BodyPersistIds.Add(pid);
                    }
                    List<IBody2> targetBodies = TopologyScanner.GetTargetSolidBodies(_model, explicitBodies);
                    if (bedEnt != null)
                    {
                        BedStateStore.SaveBedForBodies(_model, targetBodies, bedEnt, _config);
                    }

                    if (_mode == DfmCommandMode.TeardropFillet)
                    {
                        List<IFace2> selectedHoleFaces = GetSelectedEntitiesByMark<IFace2>(selMgr, MARK_TEARDROP_FACES);
                        _config.TeardropFacePersistIds.Clear();
                        foreach (IFace2 f in selectedHoleFaces)
                        {
                            byte[] pid = BedStateStore.GetPersistReferenceBytes(_model, f);
                            if (pid != null) _config.TeardropFacePersistIds.Add(pid);
                        }

                        if (selectedHoleFaces.Count > 0)
                        {
                            _pendingHoleGroups = TopologyScanner.BuildTeardropTargetsFromSelectedFaces(selectedHoleFaces, _config);
                            _config.TeardropTargets.Clear();
                            foreach (DetectedHoleFaceGroup g in _pendingHoleGroups)
                                _config.TeardropTargets.Add(g.Target);
                        }
                    }
                    else if (_mode == DfmCommandMode.CrisscrossBridge)
                    {
                        List<IFace2> selectedBridgeFaces = GetSelectedEntitiesByMark<IFace2>(selMgr, MARK_BRIDGE_FACES);
                        _config.BridgeFacePersistIds.Clear();
                        foreach (IFace2 f in selectedBridgeFaces)
                        {
                            byte[] pid = BedStateStore.GetPersistReferenceBytes(_model, f);
                            if (pid != null) _config.BridgeFacePersistIds.Add(pid);
                        }

                        if (selectedBridgeFaces.Count > 0)
                        {
                            _pendingRecesses = TopologyScanner.BuildBridgeTargetsFromSelectedFaces(selectedBridgeFaces, _config);
                            _config.BridgeTargets.Clear();
                            foreach (DetectedRecessFace r in _pendingRecesses)
                                _config.BridgeTargets.Add(r.Target);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("OnClose error: " + ex);
            }
            finally
            {
                _calloutMgr.ClearCallouts();
            }
        }

        public void AfterClose()
        {
            if (!_wasAccepted || _model == null)
                return;

            try
            {
                if (_mode == DfmCommandMode.TeardropFillet)
                {
                    List<IFeature> created = DfmFeatureBuilder.ApplyTeardropFeatures(_swApp, _model, _config, _pendingHoleGroups);
                    if (created.Count > 0 || _existingMacroFeature != null)
                    {
                        DfmMacroFeatureHelper.CommitAndNestFeatures(
                            _model, _existingMacroFeature, _config, DfmCommandMode.TeardropFillet, created);
                    }
                }
                else if (_mode == DfmCommandMode.CrisscrossBridge)
                {
                    List<IFeature> created = DfmFeatureBuilder.ApplyCrisscrossBridgeFeatures(_swApp, _model, _config, _pendingRecesses);
                    if (created.Count > 0 || _existingMacroFeature != null)
                    {
                        DfmMacroFeatureHelper.CommitAndNestFeatures(
                            _model, _existingMacroFeature, _config, DfmCommandMode.CrisscrossBridge, created);
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("AfterClose error: " + ex);
            }
        }

        public bool OnHelp() => true;
        public bool OnPreviousPage() => true;
        public bool OnNextPage() => true;
        public bool OnPreview() => true;
        public void OnWhatsNew() { }
        public void OnUndo() { }
        public void OnRedo() { }
        public bool OnTabClicked(int Id) => true;
        public void OnGroupExpand(int Id, bool Expanded) { }
        public void OnGroupCheck(int Id, bool Checked) { }

        public void OnCheckboxCheck(int Id, bool Checked)
        {
            if (Id == ID_CHK_FLIP_BED)
            {
                RefreshBedAndAutoSelect();
            }
            else if (Id == ID_CHK_CAP_HEIGHT && _numMaxCapHeight != null)
            {
                ((IPropertyManagerPageControl)_numMaxCapHeight).Enabled = Checked;
            }
        }

        public void OnOptionCheck(int Id) { }

        public void OnButtonPress(int Id)
        {
            if (Id == ID_BTN_AUTO_TEARDROP || Id == ID_BTN_AUTO_BRIDGES)
            {
                RefreshBedAndAutoSelect();
            }
        }

        public void OnTextboxChanged(int Id, string Text) { }

        public void OnNumberboxChanged(int Id, double Value)
        {
            SyncSettingsFromControls();
            if (Id == ID_NUM_MIN_CIRCLE_ANGLE)
            {
                RefreshBedAndAutoSelect();
            }
            else if (_model != null)
            {
                UpdateStatusAndCallout((ISelectionMgr)_model.SelectionManager);
            }
        }

        public void OnComboboxEditChanged(int Id, string Text) { }
        public void OnComboboxSelectionChanged(int Id, int Item) { }
        public void OnListboxSelectionChanged(int Id, int Item) { }
        public void OnSelectionboxFocusChanged(int Id) { }

        public void OnSelectionboxListChanged(int Id, int Count)
        {
            if (_isAutoPopulating) return;

            if (Id == ID_SEL_BED && Count > 0)
            {
                RefreshBedAndAutoSelect();
            }
            else if (Id == ID_SEL_BODIES)
            {
                // When user changes selected bodies, check if a saved bed exists for those bodies or refresh auto-select
                if (_model != null)
                {
                    ISelectionMgr selMgr = (ISelectionMgr)_model.SelectionManager;
                    if (_currentBedRefEntity == null)
                    {
                        List<IBody2> bodies = GetCurrentlySelectedBodies(selMgr);
                        SavedBedInfo savedBed = BedStateStore.TryLoadBedForBodies(_model, bodies);
                        if (savedBed != null)
                        {
                            object bedObj = BedStateStore.ResolvePersistReference(_model, savedBed.BedRefPersistId);
                            if (bedObj != null)
                            {
                                _isAutoPopulating = true;
                                try
                                {
                                    _currentBedRefEntity = bedObj;
                                    SelectData selData = (SelectData)selMgr.CreateSelectData();
                                    selData.Mark = MARK_BED_REF;
                                    SelectObjectWithMark(bedObj, selData);
                                }
                                finally { _isAutoPopulating = false; }
                            }
                        }
                    }
                    RefreshBedAndAutoSelect();
                }
            }
            else if (_model != null)
            {
                UpdateStatusAndCallout((ISelectionMgr)_model.SelectionManager);
            }
        }

        public void OnSelectionboxCalloutCreated(int Id) { }
        public void OnSelectionboxCalloutDestroyed(int Id) { }

        public bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText)
        {
            if (Id == ID_SEL_TEARDROP_FACES)
            {
                // Reject rectangular faces, planar faces, and 90° corner fillets in Teardrop selection
                if (!(Selection is IFace2 face) || !TopologyScanner.IsQualifyingCircularHoleFace(face, _config))
                {
                    return false;
                }
            }
            return true;
        }

        public int OnActiveXControlCreated(int Id, bool Status) => 0;
        public void OnSliderPositionChanged(int Id, double Value) { }
        public void OnSliderTrackingCompleted(int Id, double Value) { }
        public bool OnKeystroke(int Wparam, int Message, int Lparam, int Id) => true;
        public void OnPopupMenuItem(int Id) { }
        public void OnPopupMenuItemUpdate(int Id, ref int retval) { }
        public void OnGainedFocus(int Id) { }
        public void OnLostFocus(int Id) { }
        public int OnWindowFromHandleControlCreated(int Id, bool Status) => 0;
        public void OnListboxRMBUp(int Id, int PosX, int PosY) { }
        public void OnNumberBoxTrackingCompleted(int Id, double Value) { }

        #endregion
    }
}
