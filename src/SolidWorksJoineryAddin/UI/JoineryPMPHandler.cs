using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SolidWorksJoineryAddin.Data;
using SolidWorksJoineryAddin.MacroFeatures;

namespace SolidWorksJoineryAddin.UI
{
    [ComVisible(true)]
    [Guid("B6E4193F-4185-4202-9BD1-31D958FE8977")]
    public class JoineryPMPHandler : IPropertyManagerPage2Handler9
    {
        private const double INCHES_TO_METERS = 0.0254;
        private const double METERS_TO_INCHES = 1.0 / 0.0254;

        private const int MARK_BODIES = 1;
        private const int MARK_FACES = 2;

        private const int ID_GRP_GLOBAL = 100;
        private const int ID_LBL_JOINT_TYPE = 101;
        private const int ID_CMB_JOINT_TYPE = 102;
        private const int ID_LBL_TAB_COUNT = 103;
        private const int ID_NUM_TAB_COUNT = 104;
        private const int ID_LBL_TAB_WIDTH = 105;
        private const int ID_NUM_TAB_WIDTH = 106;
        private const int ID_LBL_BIT_RADIUS = 107;
        private const int ID_NUM_BIT_RADIUS = 108;
        private const int ID_LBL_CLEARANCE = 109;
        private const int ID_NUM_CLEARANCE = 110;
        private const int ID_CHK_DOGBONES = 111;
        private const int ID_LBL_RELIEF_TYPE = 112;
        private const int ID_CMB_RELIEF_TYPE = 113;
        private const int ID_CHK_KEYING = 114;

        private const int ID_GRP_ADVISORY = 200;
        private const int ID_LBL_ADVISORY = 201;
        private const int ID_OPT_MANY = 202;
        private const int ID_OPT_LIMIT_N = 203;
        private const int ID_OPT_DISABLE = 204;

        private const int ID_GRP_JOINTS = 300;
        private const int ID_LBL_BODIES = 301;
        private const int ID_SEL_BODIES = 302;
        private const int ID_LBL_FACES = 303;
        private const int ID_SEL_FACES = 304;
        private const int ID_BTN_AUTO_DETECT = 305;
        private const int ID_LBL_STATUS = 306;

        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _model;
        private readonly IFeature _existingFeature;
        private readonly JointGroupDefinition _group;
        private readonly CalloutManager _calloutMgr;

        private IPropertyManagerPage2 _page;
        private IPropertyManagerPageGroup _grpGlobal;
        private IPropertyManagerPageGroup _grpAdvisory;
        private IPropertyManagerPageGroup _grpJoints;

        // Controls
        private IPropertyManagerPageCombobox _cmbJointType;
        private IPropertyManagerPageNumberbox _numTabCount;
        private IPropertyManagerPageNumberbox _numTabWidth;
        private IPropertyManagerPageNumberbox _numBitRadius;
        private IPropertyManagerPageNumberbox _numClearance;
        private IPropertyManagerPageCheckbox _chkDogbones;
        private IPropertyManagerPageCombobox _cmbReliefType;
        private IPropertyManagerPageCheckbox _chkKeying;

        private IPropertyManagerPageLabel _lblAdvisoryText;
        private IPropertyManagerPageOption _optDisambiguateMax;
        private IPropertyManagerPageOption _optLimitToN;
        private IPropertyManagerPageOption _optDisableKeying;

        private IPropertyManagerPageSelectionbox _selBodies;
        private IPropertyManagerPageSelectionbox _selFaces;
        private IPropertyManagerPageButton _btnAutoDetect;
        private IPropertyManagerPageLabel _lblStatus;

        private bool _isAutoPopulating;
        private bool _wasAccepted;

        public JoineryPMPHandler(
            ISldWorks swApp,
            IModelDoc2 model,
            IFeature existingFeature = null,
            JointGroupDefinition group = null)
        {
            _swApp = swApp;
            _model = model;
            _existingFeature = existingFeature;
            _group = group ?? ProjectSettings.CreateDefaultGroup();
            _calloutMgr = new CalloutManager(swApp, model);
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

                _page = (IPropertyManagerPage2)_swApp.CreatePropertyManagerPage("Multi-Body Joinery Manager", options, this, ref errors);
                if (_page == null || errors != 0)
                {
                    SwAddin.Log("CreatePropertyManagerPage failed with error code: " + errors);
                    return;
                }

                BuildUI();
                int showRes = _page.Show();
                SwAddin.Log(string.Format("Joinery PropertyManagerPage.Show(isEdit={0}) returned: {1}",
                    _existingFeature != null, showRes));
            }
            catch (Exception ex)
            {
                SwAddin.Log("JoineryPMPHandler.Show() error: " + ex);
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

            // 1. Global Settings Group
            _grpGlobal = (IPropertyManagerPageGroup)_page.AddGroupBox(ID_GRP_GLOBAL, "Global Joint Settings", grpOpts);

            _grpGlobal.AddControl2(
                ID_LBL_JOINT_TYPE,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Joint Preset Type:", alignLeft, ctrlOpts, "");
            _cmbJointType = (IPropertyManagerPageCombobox)_grpGlobal.AddControl2(
                ID_CMB_JOINT_TYPE,
                (short)swPropertyManagerPageControlType_e.swControlType_Combobox,
                "Joint Preset Type", alignLeft, ctrlOpts,
                "Select joint type for selected mating bodies.");
            if (_cmbJointType != null)
            {
                _cmbJointType.AddItems(new[]
                {
                    "Tab & Slot",
                    "Finger / Box Joint",
                    "Through Dado",
                    "Blind Dado",
                    "Rabbet",
                    "Dado + Tab Combo"
                });
                _cmbJointType.CurrentSelection = (short)Clamp((int)_group.DefaultType, 0, 5);
            }

            _grpGlobal.AddControl2(
                ID_LBL_TAB_COUNT,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Tabs / Fingers per Joint:", alignLeft, ctrlOpts, "");
            _numTabCount = (IPropertyManagerPageNumberbox)_grpGlobal.AddControl2(
                ID_NUM_TAB_COUNT,
                (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "Tabs / Fingers per Joint", alignLeft, ctrlOpts,
                "Number of tabs or interlocking fingers per joint.");
            if (_numTabCount != null)
            {
                _numTabCount.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessInteger, 1, 50, 1, true);
                _numTabCount.Value = Clamp(_group.DefaultTabCount, 1, 50);
            }

            _grpGlobal.AddControl2(
                ID_LBL_TAB_WIDTH,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Nominal Tab Width (in):", alignLeft, ctrlOpts, "");
            _numTabWidth = (IPropertyManagerPageNumberbox)_grpGlobal.AddControl2(
                ID_NUM_TAB_WIDTH,
                (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "Nominal Tab Width (in)", alignLeft, ctrlOpts,
                "Base tab width in inches prior to directional keying.");
            if (_numTabWidth != null)
            {
                _numTabWidth.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, 0.05, 24.0, 0.125, true);
                _numTabWidth.Value = Clamp(_group.DefaultTabWidth * METERS_TO_INCHES, 0.05, 24.0);
            }

            _grpGlobal.AddControl2(
                ID_LBL_BIT_RADIUS,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Tool Bit Radius (in):", alignLeft, ctrlOpts, "");
            _numBitRadius = (IPropertyManagerPageNumberbox)_grpGlobal.AddControl2(
                ID_NUM_BIT_RADIUS,
                (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "Tool Bit Radius (in)", alignLeft, ctrlOpts,
                "CNC router bit radius for diagonal dogbone corner reliefs (default 0.25 in).");
            if (_numBitRadius != null)
            {
                _numBitRadius.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, 0.03125, 2.0, 0.0625, true);
                _numBitRadius.Value = Clamp(_group.DefaultToolBitRadius * METERS_TO_INCHES, 0.03125, 2.0);
            }

            _grpGlobal.AddControl2(
                ID_LBL_CLEARANCE,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Signed Fit Clearance (+gap / -press, in):", alignLeft, ctrlOpts, "");
            _numClearance = (IPropertyManagerPageNumberbox)_grpGlobal.AddControl2(
                ID_NUM_CLEARANCE,
                (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "Signed Fit Clearance (in)", alignLeft, ctrlOpts,
                "Positive for sliding clearance in rigid stock; negative for press-fit interference.");
            if (_numClearance != null)
            {
                _numClearance.SetRange((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, -0.05, 0.05, 0.001, true);
                _numClearance.Value = Clamp(_group.DefaultSignedClearance * METERS_TO_INCHES, -0.05, 0.05);
            }

            _chkDogbones = (IPropertyManagerPageCheckbox)_grpGlobal.AddControl2(
                ID_CHK_DOGBONES,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Enable Dogbone Corner Relief", alignLeft, ctrlOpts,
                "Toggle CNC overcut reliefs on interior slot and blind dado corners.");
            if (_chkDogbones != null)
                _chkDogbones.Checked = _group.MasterEnableDogbones;

            _grpGlobal.AddControl2(
                ID_LBL_RELIEF_TYPE,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Corner Relief Style:", alignLeft, ctrlOpts, "");
            _cmbReliefType = (IPropertyManagerPageCombobox)_grpGlobal.AddControl2(
                ID_CMB_RELIEF_TYPE,
                (short)swPropertyManagerPageControlType_e.swControlType_Combobox,
                "Corner Relief Style", alignLeft, ctrlOpts,
                "Choose 45 deg diagonal dogbones, T-bone, or sharp corners.");
            if (_cmbReliefType != null)
            {
                _cmbReliefType.AddItems(new[] { "Diagonal Dogbone (45°)", "T-Bone", "None (Sharp)" });
                _cmbReliefType.CurrentSelection = (short)Clamp((int)_group.DefaultReliefType, 0, 2);
                ((IPropertyManagerPageControl)_cmbReliefType).Enabled = _group.MasterEnableDogbones;
            }

            if (_numBitRadius != null)
                ((IPropertyManagerPageControl)_numBitRadius).Enabled = _group.MasterEnableDogbones;

            _chkKeying = (IPropertyManagerPageCheckbox)_grpGlobal.AddControl2(
                ID_CHK_KEYING,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Enable Poka-Yoke Keying (0.5\" Directional)", alignLeft, ctrlOpts,
                "Automatically offset confusable joints by 0.5 in opposite directions so assembly is unambiguous.");
            if (_chkKeying != null)
                _chkKeying.Checked = _group.MasterEnableKeying;

            // 2. Advisory Badge Group (Visible when tab disambiguation capacity is exceeded)
            _grpAdvisory = (IPropertyManagerPageGroup)_page.AddGroupBox(
                ID_GRP_ADVISORY, "⚠️ Disambiguation Advisory", grpOpts);

            _lblAdvisoryText = (IPropertyManagerPageLabel)_grpAdvisory.AddControl2(
                ID_LBL_ADVISORY,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Ready. Analyzing joint confusions...", alignLeft, ctrlOpts, "");

            _optDisambiguateMax = (IPropertyManagerPageOption)_grpAdvisory.AddControl2(
                ID_OPT_MANY,
                (short)swPropertyManagerPageControlType_e.swControlType_Option,
                "Disambiguate as many as possible", alignLeft, ctrlOpts,
                "Keys up to 2 * N_min joints; remaining joints retain base profile.");
            _optLimitToN = (IPropertyManagerPageOption)_grpAdvisory.AddControl2(
                ID_OPT_LIMIT_N,
                (short)swPropertyManagerPageControlType_e.swControlType_Option,
                "Limit to joints with >= N tabs", alignLeft, ctrlOpts,
                "Excludes joints with fewer than N tabs so qualified joints are 100% disambiguated.");
            _optDisableKeying = (IPropertyManagerPageOption)_grpAdvisory.AddControl2(
                ID_OPT_DISABLE,
                (short)swPropertyManagerPageControlType_e.swControlType_Option,
                "Force disable keying for this group", alignLeft, ctrlOpts,
                "Leaves this confusable group unkeyed.");

            if (_optDisambiguateMax != null)
                _optDisambiguateMax.Checked = (_group.DisambiguationFallback == DisambiguationChoice.DisambiguateAsManyAsPossible);
            if (_optLimitToN != null)
                _optLimitToN.Checked = (_group.DisambiguationFallback == DisambiguationChoice.LimitToQualifiedJoints);
            if (_optDisableKeying != null)
                _optDisableKeying.Checked = (_group.DisambiguationFallback == DisambiguationChoice.ForceDisable);

            // 3. Joints & Bodies Selection Group
            _grpJoints = (IPropertyManagerPageGroup)_page.AddGroupBox(
                ID_GRP_JOINTS, "Joint Pairs & Mating Bodies", grpOpts);

            _grpJoints.AddControl2(
                ID_LBL_BODIES,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Target Solid Bodies (Leave empty for All):", alignLeft, ctrlOpts, "");
            _selBodies = (IPropertyManagerPageSelectionbox)_grpJoints.AddControl2(
                ID_SEL_BODIES,
                (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                "Target Solid Bodies", alignLeft, ctrlOpts,
                "Select solid bodies to participate in joinery, or leave empty for all solid bodies.");
            if (_selBodies != null)
            {
                _selBodies.SetSelectionFilters(new[] { (int)swSelectType_e.swSelSOLIDBODIES });
                _selBodies.SingleEntityOnly = false;
                _selBodies.Mark = MARK_BODIES;
                _selBodies.Height = 36;
            }

            _grpJoints.AddControl2(
                ID_LBL_FACES,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Mating Contact Faces or Edges:", alignLeft, ctrlOpts, "");
            _selFaces = (IPropertyManagerPageSelectionbox)_grpJoints.AddControl2(
                ID_SEL_FACES,
                (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                "Mating Contact Faces or Edges", alignLeft, ctrlOpts,
                "Select contacting faces or edges across bodies, or click Auto-Detect.");
            if (_selFaces != null)
            {
                _selFaces.SetSelectionFilters(new[] { (int)swSelectType_e.swSelFACES, (int)swSelectType_e.swSelEDGES });
                _selFaces.SingleEntityOnly = false;
                _selFaces.AllowMultipleSelectOfSameEntity = false;
                _selFaces.Mark = MARK_FACES;
                _selFaces.Height = 65;
            }

            _btnAutoDetect = (IPropertyManagerPageButton)_grpJoints.AddControl2(
                ID_BTN_AUTO_DETECT,
                (short)swPropertyManagerPageControlType_e.swControlType_Button,
                "Auto-Detect Mating Joints", alignLeft, ctrlOpts,
                "Scan selected/all solid bodies for contacting planar joints.");

            _lblStatus = (IPropertyManagerPageLabel)_grpJoints.AddControl2(
                ID_LBL_STATUS,
                (short)swPropertyManagerPageControlType_e.swControlType_Label,
                "Select contact faces or click Auto-Detect Mating Joints.", alignLeft, ctrlOpts, "");

            UpdateAdvisoryState();
        }

        private void SyncSettingsFromControls()
        {
            try
            {
                if (_cmbJointType != null && _cmbJointType.CurrentSelection >= 0)
                    _group.DefaultType = (JointType)_cmbJointType.CurrentSelection;
                if (_numTabCount != null)
                    _group.DefaultTabCount = Math.Max(1, (int)Math.Round(_numTabCount.Value));
                if (_numTabWidth != null)
                    _group.DefaultTabWidth = _numTabWidth.Value * INCHES_TO_METERS;
                if (_numBitRadius != null)
                    _group.DefaultToolBitRadius = _numBitRadius.Value * INCHES_TO_METERS;
                if (_numClearance != null)
                    _group.DefaultSignedClearance = _numClearance.Value * INCHES_TO_METERS;
                if (_chkDogbones != null)
                    _group.MasterEnableDogbones = _chkDogbones.Checked;
                if (_cmbReliefType != null && _cmbReliefType.CurrentSelection >= 0)
                    _group.DefaultReliefType = (CornerReliefType)_cmbReliefType.CurrentSelection;
                if (_chkKeying != null)
                    _group.MasterEnableKeying = _chkKeying.Checked;

                if (_optLimitToN != null && _optLimitToN.Checked)
                    _group.DisambiguationFallback = DisambiguationChoice.LimitToQualifiedJoints;
                else if (_optDisableKeying != null && _optDisableKeying.Checked)
                    _group.DisambiguationFallback = DisambiguationChoice.ForceDisable;
                else
                    _group.DisambiguationFallback = DisambiguationChoice.DisambiguateAsManyAsPossible;

                // Propagate global settings onto active joints in the group
                if (_group.Joints != null)
                {
                    foreach (JointDefinition j in _group.Joints)
                    {
                        j.Type = _group.DefaultType;
                        j.TabCount = _group.DefaultTabCount;
                        j.NominalTabWidth = _group.DefaultTabWidth;
                        j.ToolBitRadius = _group.DefaultToolBitRadius;
                        j.SignedClearance = _group.DefaultSignedClearance;
                        j.EnableDogbones = _group.MasterEnableDogbones;
                        j.ReliefType = _group.DefaultReliefType;
                        j.EnableKeying = _group.MasterEnableKeying;
                        j.KeyOffset = _group.DefaultKeyDelta;
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("SyncSettingsFromControls error: " + ex);
            }
        }

        private void RebuildJointsFromSelectionsOrAuto(bool forceAutoScan)
        {
            if (_model == null || _isAutoPopulating) return;

            try
            {
                SyncSettingsFromControls();

                ISelectionMgr selMgr = (ISelectionMgr)_model.SelectionManager;
                List<IBody2> explicitBodies = GetSelectedEntitiesByMark<IBody2>(selMgr, MARK_BODIES);
                List<IBody2> targetBodies = JoineryTopologyScanner.GetTargetSolidBodies(_model, explicitBodies);

                List<IFace2> selFaces = GetSelectedEntitiesByMark<IFace2>(selMgr, MARK_FACES);
                List<IEdge> selEdges = GetSelectedEntitiesByMark<IEdge>(selMgr, MARK_FACES);

                List<DetectedJointContact> contacts;
                if (forceAutoScan || (selFaces.Count == 0 && selEdges.Count == 0))
                {
                    contacts = JoineryTopologyScanner.ScanContactingBodyPairs(_model, targetBodies, _group);
                    if (forceAutoScan && contacts.Count > 0)
                    {
                        _isAutoPopulating = true;
                        try
                        {
                            ClearSelectionByMark(selMgr, MARK_FACES);
                            SelectData selData = (SelectData)selMgr.CreateSelectData();
                            selData.Mark = MARK_FACES;
                            foreach (DetectedJointContact c in contacts)
                            {
                                if (c.SourceFace is IEntity ent)
                                {
                                    ent.Select4(true, selData);
                                }
                            }
                        }
                        finally
                        {
                            _isAutoPopulating = false;
                        }
                    }
                }
                else
                {
                    contacts = JoineryTopologyScanner.BuildJointsFromSelectedEntities(
                        _model, selFaces, selEdges, targetBodies, _group);
                }

                _group.Joints.Clear();
                foreach (DetectedJointContact c in contacts)
                {
                    _group.Joints.Add(c.Joint);
                }

                UpdateAdvisoryState();
            }
            catch (Exception ex)
            {
                SwAddin.Log("RebuildJointsFromSelectionsOrAuto error: " + ex);
            }
        }

        private void UpdateAdvisoryState()
        {
            try
            {
                KeyingEngine.ResolveKeying(_group);

                bool hasCapacityExceeded = false;
                string advisoryMsg = "";
                int requiredN = 0;

                foreach (JointDefinition j in _group.Joints)
                {
                    if (j.HasConflict && j.ConflictDescription.Contains("⚠️"))
                    {
                        hasCapacityExceeded = true;
                        advisoryMsg = j.ConflictDescription;
                        int eqIdx = advisoryMsg.LastIndexOf("N=");
                        if (eqIdx >= 0)
                        {
                            string tail = advisoryMsg.Substring(eqIdx + 2).TrimEnd('.');
                            int.TryParse(tail, out requiredN);
                        }
                        break;
                    }
                }

                if (_grpAdvisory != null)
                {
                    _grpAdvisory.Visible = hasCapacityExceeded;
                }

                if (hasCapacityExceeded)
                {
                    if (_lblAdvisoryText != null)
                        _lblAdvisoryText.Caption = advisoryMsg;
                    if (_optLimitToN != null && requiredN > 0)
                        _optLimitToN.Caption = string.Format("Limit to joints with >= {0} tabs (N={0})", requiredN);
                }

                // Refresh 3D Callouts & status label
                _calloutMgr.ClearCallouts();
                int keyedCount = 0;
                foreach (JointDefinition j in _group.Joints)
                {
                    if (j.IsKeyed) keyedCount++;
                    _calloutMgr.UpdateCallout(j);
                }

                if (_lblStatus != null)
                {
                    _lblStatus.Caption = string.Format("{0} Joint(s) Ready | {1} Poka-Yoke Keyed",
                        _group.Joints.Count, keyedCount);
                }
            }
            catch { }
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

        private static void SelectObjectWithMark(object obj, SelectData selData)
        {
            if (obj is IEntity ent)
            {
                ent.Select4(true, selData);
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
                if (_existingFeature != null)
                {
                    SelectData selData = (SelectData)selMgr.CreateSelectData();

                    if (_group.BodyPersistIds != null)
                    {
                        selData.Mark = MARK_BODIES;
                        foreach (byte[] pid in _group.BodyPersistIds)
                        {
                            object bObj = JoineryTopologyScanner.ResolvePersistReference(_model, pid);
                            if (bObj != null) SelectObjectWithMark(bObj, selData);
                        }
                    }

                    if (_group.SelectedFacePersistIds != null)
                    {
                        selData.Mark = MARK_FACES;
                        foreach (byte[] pid in _group.SelectedFacePersistIds)
                        {
                            object fObj = JoineryTopologyScanner.ResolvePersistReference(_model, pid);
                            if (fObj != null) SelectObjectWithMark(fObj, selData);
                        }
                    }
                }

                UpdateAdvisoryState();
            }
            catch (Exception ex)
            {
                SwAddin.Log("AfterActivation error: " + ex);
            }
            finally
            {
                _isAutoPopulating = false;
            }

            if (_selFaces != null)
            {
                _selFaces.SetSelectionFocus();
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

                    ISelectionMgr selMgr = (ISelectionMgr)_model.SelectionManager;

                    // Capture body selections
                    List<IBody2> explicitBodies = GetSelectedEntitiesByMark<IBody2>(selMgr, MARK_BODIES);
                    _group.BodyPersistIds.Clear();
                    foreach (IBody2 b in explicitBodies)
                    {
                        byte[] pid = JoineryTopologyScanner.GetPersistReferenceBytes(_model, b);
                        if (pid != null) _group.BodyPersistIds.Add(pid);
                    }

                    // Capture face/edge selections
                    List<IFace2> selFaces = GetSelectedEntitiesByMark<IFace2>(selMgr, MARK_FACES);
                    List<IEdge> selEdges = GetSelectedEntitiesByMark<IEdge>(selMgr, MARK_FACES);
                    _group.SelectedFacePersistIds.Clear();
                    foreach (IFace2 f in selFaces)
                    {
                        byte[] pid = JoineryTopologyScanner.GetPersistReferenceBytes(_model, f);
                        if (pid != null) _group.SelectedFacePersistIds.Add(pid);
                    }

                    // Build/refresh joints list prior to closing PMP so selection manager is still alive
                    if (_group.Joints.Count == 0 || selFaces.Count > 0 || selEdges.Count > 0)
                    {
                        List<IBody2> targetBodies = JoineryTopologyScanner.GetTargetSolidBodies(_model, explicitBodies);
                        List<DetectedJointContact> contacts = (selFaces.Count > 0 || selEdges.Count > 0)
                            ? JoineryTopologyScanner.BuildJointsFromSelectedEntities(_model, selFaces, selEdges, targetBodies, _group)
                            : JoineryTopologyScanner.ScanContactingBodyPairs(_model, targetBodies, _group);

                        if (contacts.Count > 0)
                        {
                            _group.Joints.Clear();
                            foreach (DetectedJointContact c in contacts)
                            {
                                _group.Joints.Add(c.Joint);
                            }
                        }
                    }

                    // Persist project defaults
                    ProjectSettings.DefaultJointType = _group.DefaultType;
                    ProjectSettings.DefaultTabCount = _group.DefaultTabCount;
                    ProjectSettings.DefaultTabWidth = _group.DefaultTabWidth;
                    ProjectSettings.DefaultToolBitRadius = _group.DefaultToolBitRadius;
                    ProjectSettings.DefaultSignedClearance = _group.DefaultSignedClearance;
                    ProjectSettings.DefaultEnableDogbones = _group.MasterEnableDogbones;
                    ProjectSettings.DefaultReliefType = _group.DefaultReliefType;
                    ProjectSettings.DefaultEnableKeying = _group.MasterEnableKeying;
                    ProjectSettings.Save();

                    KeyingEngine.ResolveKeying(_group);
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
                List<IFeature> created = GeometryBuilder.ApplyJointGroupToModel(_swApp, _model, _group);
                if (created.Count > 0 || _existingFeature != null)
                {
                    JoineryMacroFeatureHelper.CommitAndNestFeatures(_model, _existingFeature, _group, created);
                }
                else
                {
                    SwAddin.Log("AfterClose: No joints or features generated.");
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
            if (Id == ID_CHK_DOGBONES)
            {
                if (_cmbReliefType != null)
                    ((IPropertyManagerPageControl)_cmbReliefType).Enabled = Checked;
                if (_numBitRadius != null)
                    ((IPropertyManagerPageControl)_numBitRadius).Enabled = Checked;
            }

            SyncSettingsFromControls();
            UpdateAdvisoryState();
        }

        public void OnOptionCheck(int Id)
        {
            SyncSettingsFromControls();
            UpdateAdvisoryState();
        }

        public void OnButtonPress(int Id)
        {
            if (Id == ID_BTN_AUTO_DETECT)
            {
                RebuildJointsFromSelectionsOrAuto(forceAutoScan: true);
            }
        }

        public void OnTextboxChanged(int Id, string Text) { }

        public void OnNumberboxChanged(int Id, double Value)
        {
            SyncSettingsFromControls();
            UpdateAdvisoryState();
        }

        public void OnComboboxEditChanged(int Id, string Text) { }

        public void OnComboboxSelectionChanged(int Id, int Item)
        {
            SyncSettingsFromControls();
            UpdateAdvisoryState();
        }

        public void OnListboxSelectionChanged(int Id, int Item) { }
        public void OnSelectionboxFocusChanged(int Id) { }

        public void OnSelectionboxListChanged(int Id, int Count)
        {
            if (_isAutoPopulating) return;

            if (Id == ID_SEL_FACES || Id == ID_SEL_BODIES)
            {
                RebuildJointsFromSelectionsOrAuto(forceAutoScan: false);
            }
        }

        public void OnSelectionboxCalloutCreated(int Id) { }
        public void OnSelectionboxCalloutDestroyed(int Id) { }
        public bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText) => true;
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
        public void OnNumberBoxTrackingCompleted(int Id, double Value)
        {
            SyncSettingsFromControls();
            UpdateAdvisoryState();
        }
        #endregion
    }
}
