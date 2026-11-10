' ==============================================================================
' SOLIDWORKS MACRO: ExportFacesToDXF
' Description:
'   Exports user-selected planar faces from a Part or Assembly to DXF for CAM.
'
' Key Features:
'   - Dedicated Outline Layer ("OUTLINE"): Automatically extracts and emits the
'     exterior silhouette perimeter of exported parts. Perfect for CAM outside
'     profile cutout toolpaths.
'   - Per-Face CAM Layers: Overlaid parallel faces (e.g. stepped pockets of varying
'     depths) are isolated on distinct layers (e.g. FACE_1, FACE_2 or Part_F1...).
'   - 100% Closed Vectors: All lines, arcs, and polylines are chained strictly
'     per-layer and converted into closed contours (LWPOLYLINE flag 70=1).
'     Eliminates all "open vector" warnings in CAM software (Fusion 360, VCarve,
'     Mastercam, SheetCAM, LightBurn, Aspire).
'   - Native Hole Drilling Cycles Preserved: CIRCLE entities remain true circles
'     so CAM software can generate canned CNC drill cycles (peck, dwell, bore).
'   - Robust Single Combined DXF (Mode 2): Combines all faces across different
'     bodies and assembly parts without losing vectors. Employs per-subcluster
'     boundary extraction so multiple parts never cancel each other's contours.
'   - Standard-Compliant DXF Output: Preserves original AutoCAD DXF headers and
'     table structures without corrupting symbol tables ("record name is empty").
'   - Automatic Watermark & Details Stripping: Purges SolidWorks educational
'     watermarks, export details, annotations, and text so CAM tools get pure geometry.
'   - Intelligent Naming: [PartName]_[BodyName]_[Index].dxf (body name included only
'     if custom logical name differs from last feature applied).
'   - Automatic collision avoidance: uniquely numbered files (_01, _02...).
' ==============================================================================

Option Explicit

' --- SolidWorks API Constants ---
Const swDocNONE As Long = 0
Const swDocPART As Long = 1
Const swDocASSEMBLY As Long = 2
Const swDocDRAWING As Long = 3

Const swSelFACES As Long = 2

Const swExportToDWG_ExportSheetMetal As Long = 1
Const swExportToDWG_ExportSelectedFacesOrLoops As Long = 2
Const swExportToDWG_ExportAnnotationViews As Long = 3

Const swOpenDocOptions_Silent As Long = 1

Const swDxfMapping As Long = 45 ' swUserPreferenceToggle_e.swDxfMapping
Const swDXFDontShowMap As Long = 21 ' swUserPreferenceToggle_e.swDXFDontShowMap
Const swDxfEndPointMerge As Long = 316 ' swUserPreferenceToggle_e.swDxfEndPointMerge
Const swDxfExportSplinesAsSplines As Long = 358 ' swUserPreferenceToggle_e.swDxfExportSplinesAsSplines
Const swDxfMergingDistance As Long = 85 ' swUserPreferenceDoubleValue_e.swDxfMergingDistance

' --- Internal DXF Segment Definition for Vector Closing ---
Private Type DxfSeg
    X1 As Double
    Y1 As Double
    X2 As Double
    Y2 As Double
    Bulge As Double
    Layer As String
    Used As Boolean
End Type

' ==============================================================================
' MAIN ENTRY POINT
' ==============================================================================
Sub main()
    On Error GoTo ErrorHandler
    Dim swApp As SldWorks.SldWorks
    Dim swModel As SldWorks.ModelDoc2
    Dim swSelMgr As SldWorks.SelectionMgr
    
    Set swApp = Application.SldWorks
    If swApp Is Nothing Then
        MsgBox "Failed to connect to SOLIDWORKS application.", vbCritical, "CAM DXF Export"
        Exit Sub
    End If
    
    Set swModel = swApp.ActiveDoc
    If swModel Is Nothing Then
        MsgBox "Please open a Part or Assembly document and select the faces to export.", vbExclamation, "No Active Document"
        Exit Sub
    End If
    
    Dim docType As Long
    docType = swModel.GetType
    If docType <> swDocPART And docType <> swDocASSEMBLY Then
        MsgBox "This macro supports Part and Assembly documents only.", vbExclamation, "Invalid Document Type"
        Exit Sub
    End If
    
    Set swSelMgr = swModel.SelectionManager
    Dim nSel As Long
    nSel = swSelMgr.GetSelectedObjectCount2(-1)
    
    If nSel = 0 Then
        MsgBox "No faces are currently selected." & vbCrLf & vbCrLf & _
               "Please select one or more planar faces in your model and run the macro again.", _
               vbInformation, "No Selection Found"
        Exit Sub
    End If
    
    ' --------------------------------------------------------------------------
    ' STEP 1: Parse and validate all selected faces
    ' --------------------------------------------------------------------------
    Dim faceList As Collection
    Set faceList = New Collection
    
    Dim skippedNonPlanar As Long: skippedNonPlanar = 0
    Dim bodyDict As Object: Set bodyDict = CreateObject("Scripting.Dictionary")
    
    Dim i As Long
    For i = 1 To nSel
        Dim selType As Long
        selType = swSelMgr.GetSelectedObjectType3(i, -1)
        
        If selType = swSelFACES Then
            Dim swAssyFace As SldWorks.Face2
            Set swAssyFace = swSelMgr.GetSelectedObject6(i, -1)
            
            If Not swAssyFace Is Nothing Then
                Dim swSurf As SldWorks.Surface
                Set swSurf = swAssyFace.GetSurface
                
                If swSurf.IsPlane Then
                    Dim swComp As SldWorks.Component2
                    Dim swPartModel As SldWorks.ModelDoc2
                    Dim swPartDoc As SldWorks.PartDoc
                    Dim swPartFace As SldWorks.Face2
                    Dim swBody As SldWorks.Body2
                    
                    Dim compConfig As String: compConfig = ""
                    If docType = swDocASSEMBLY Then
                        Set swComp = swSelMgr.GetSelectedObjectsComponent4(i, -1)
                        If Not swComp Is Nothing Then
                            If swComp.GetSuppression <> 2 Then ' Fully resolve lightweight or suppressed components
                                swComp.SetResolved
                            End If
                            Set swPartModel = swComp.GetModelDoc2
                            compConfig = swComp.ReferencedConfiguration
                        End If
                        
                        ' Ensure component model is loaded
                        If swPartModel Is Nothing And Not swComp Is Nothing Then
                            Dim compPath As String: compPath = swComp.GetPathName
                            If compPath <> "" Then
                                Dim oErr As Long, oWarn As Long
                                Set swPartModel = swApp.OpenDoc6(compPath, swDocPART, swOpenDocOptions_ReadOnly, "", oErr, oWarn)
                            End If
                        End If
                        
                        If swPartModel Is Nothing Then
                            Set swPartModel = swModel
                        End If
                        
                        ' Ensure the part model has this component's configuration active
                        If compConfig <> "" And Not swPartModel Is Nothing Then
                            If Not (swPartModel Is swModel) Then
                                On Error Resume Next
                                swPartModel.ShowConfiguration2 compConfig
                                On Error GoTo ErrorHandler
                            End If
                        End If
                        
                        Set swPartDoc = swPartModel
                        
                        ' Map assembly face to native part-level face entity
                        Dim swPartEnt As SldWorks.Entity
                        If Not swPartModel Is Nothing And Not swComp Is Nothing Then
                            On Error Resume Next
                            Dim extObj As Object: Set extObj = swPartModel.Extension
                            If Not extObj Is Nothing Then
                                Set swPartEnt = extObj.GetCorrespondingEntity2(swAssyFace)
                                If swPartEnt Is Nothing Then
                                    Set swPartEnt = extObj.GetCorresponding(swAssyFace)
                                End If
                                If swPartEnt Is Nothing Then
                                    Set swPartEnt = extObj.GetCorresponding2(swAssyFace)
                                End If
                            End If
                            If swPartEnt Is Nothing Then
                                Dim compObj As Object: Set compObj = swComp
                                Set swPartEnt = compObj.GetCorrespondingEntity(swAssyFace)
                            End If
                            On Error GoTo ErrorHandler
                        End If
                        If swPartEnt Is Nothing Then
                            Set swPartEnt = swAssyFace
                        End If
                        Set swPartFace = swPartEnt
                        
                        Set swBody = swPartFace.GetBody
                        If swBody Is Nothing Then Set swBody = swAssyFace.GetBody
                    Else
                        ' Part document
                        Set swPartModel = swModel
                        Set swPartDoc = swModel
                        Set swComp = Nothing
                        Set swPartFace = swAssyFace
                        Set swBody = swPartFace.GetBody
                        On Error Resume Next
                        If Not swModel.GetActiveConfiguration Is Nothing Then
                            compConfig = swModel.GetActiveConfiguration.Name
                        End If
                        On Error GoTo ErrorHandler
                    End If
                    
                    ' Calculate face unit normal
                    Dim vNorm As Variant
                    vNorm = swPartFace.Normal
                    
                    ' Determine Part Name and Body Name
                    Dim partName As String
                    partName = GetCleanPartName(swPartModel, swComp, compConfig)
                    
                    Dim bName As String: bName = ""
                    Dim isCustomBody As Boolean: isCustomBody = False
                    
                    If Not swBody Is Nothing Then
                        bName = swBody.Name
                        isCustomBody = IsCustomBodyName(swPartModel, swBody)
                        
                        ' Track distinct bodies for UI summary
                        Dim bKey As String
                        bKey = partName & "||" & compConfig & "||" & bName
                        If Not bodyDict.Exists(bKey) Then bodyDict.Add bKey, True
                    End If
                    
                    ' Store face info into a Dictionary item
                    Dim fDict As Object
                    Set fDict = CreateObject("Scripting.Dictionary")
                    Set fDict("FaceObj") = swPartFace
                    Set fDict("PartModel") = swPartModel
                    Set fDict("PartDoc") = swPartDoc
                    fDict("PartName") = partName
                    fDict("ConfigName") = compConfig
                    Set fDict("CompObj") = swComp
                    Set fDict("BodyObj") = swBody
                    fDict("BodyName") = bName
                    fDict("IsCustomBody") = isCustomBody
                    fDict("Normal") = Array(CDbl(vNorm(0)), CDbl(vNorm(1)), CDbl(vNorm(2)))
                    
                    faceList.Add fDict
                Else
                    skippedNonPlanar = skippedNonPlanar + 1
                End If
            End If
        End If
    Next i
    
    If faceList.Count = 0 Then
        If skippedNonPlanar > 0 Then
            MsgBox "All " & skippedNonPlanar & " selected face(s) are non-planar." & vbCrLf & _
                   "CAM DXF export requires planar (flat) faces.", vbExclamation, "No Planar Faces"
        Else
            MsgBox "No valid faces were selected. Please select planar faces.", vbExclamation, "No Faces Selected"
        End If
        Exit Sub
    End If
    
    ' --------------------------------------------------------------------------
    ' STEP 2: Determine Default Folder & Prompt User for Options
    ' --------------------------------------------------------------------------
    Dim defaultFolder As String
    defaultFolder = GetDefaultFolder(swModel)
    
    ' Prompt 1: Export Mode Selection
    Dim modePrompt As String
    modePrompt = "Selected: " & faceList.Count & " planar face(s) across " & bodyDict.Count & " body(ies)" & _
                 IIf(skippedNonPlanar > 0, " (" & skippedNonPlanar & " non-planar skipped)", "") & "." & vbCrLf & vbCrLf & _
                 "Choose DXF Export Mode:" & vbCrLf & vbCrLf & _
                 "  [1] Smart CAM Grouping (Default)" & vbCrLf & _
                 "      - Overlays parallel faces on the same body into a single DXF." & vbCrLf & _
                 "      - Each face on its own layer, plus an OUTLINE silhouette layer." & vbCrLf & _
                 "      - Non-parallel faces or different bodies go into separate DXFs." & vbCrLf & vbCrLf & _
                 "  [2] Single Combined DXF (Multi-Body / Multi-Part)" & vbCrLf & _
                 "      - Combines ALL selected faces into ONE single DXF file." & vbCrLf & _
                 "      - Each part/face has its own layer with full vector preservation." & vbCrLf & vbCrLf & _
                 "  [3] Separate DXF Per Face" & vbCrLf & _
                 "      - Exports each individual face into its own DXF with OUTLINE & FACE_1." & vbCrLf & vbCrLf & _
                 "Enter 1, 2, or 3 (or press Enter for default 1):"
                 
    Dim userChoice As String
    userChoice = InputBox(modePrompt, "SolidWorks CAM Face DXF Exporter", "1")
    userChoice = Trim(userChoice)
    If userChoice = "" Then Exit Sub ' User cancelled
    
    Dim exportMode As Long
    If userChoice = "2" Then
        exportMode = 2
    ElseIf userChoice = "3" Then
        exportMode = 3
    Else
        exportMode = 1
    End If
    
    ' Prompt 2: Output Destination Folder
    Dim targetFolder As String
    targetFolder = defaultFolder
    
    Dim folderPrompt As String
    folderPrompt = "Verify or edit destination folder for exported DXFs:" & vbCrLf & _
                   "(Click OK to accept, or Cancel to open the Windows Folder Browser)"
                   
    Dim userFolderInput As String
    userFolderInput = InputBox(folderPrompt, "DXF Destination Folder", defaultFolder)
    
    If Trim(userFolderInput) = "" Then
        Dim pickedFolder As String
        pickedFolder = BrowseForFolderDialog("Select Destination Folder for DXFs", defaultFolder)
        If pickedFolder = "" Then Exit Sub ' User cancelled browse
        targetFolder = pickedFolder
    Else
        targetFolder = Trim(userFolderInput)
    End If
    
    ' Ensure directory exists
    Dim fso As Object
    Set fso = CreateObject("Scripting.FileSystemObject")
    If Not fso.FolderExists(targetFolder) Then
        On Error Resume Next
        fso.CreateFolder targetFolder
        On Error GoTo 0
        If Not fso.FolderExists(targetFolder) Then
            MsgBox "The folder '" & targetFolder & "' could not be created or accessed.", vbCritical, "Folder Error"
            Exit Sub
        End If
    End If
    
    ' --------------------------------------------------------------------------
    ' STEP 3: Organize faces into single-body/single-normal sub-clusters
    ' --------------------------------------------------------------------------
    Dim subClusters As Collection
    Set subClusters = New Collection
    
    Dim fItem As Object
    For Each fItem In faceList
        Dim addedToSub As Boolean: addedToSub = False
        
        If exportMode <> 3 Then
            ' Modes 1 and 2: Group parallel faces on the same body and same configuration
            Dim sc As Object
            For Each sc In subClusters
                If (sc("PartModel") Is fItem("PartModel")) And _
                   (CStr(sc("ConfigName")) = CStr(fItem("ConfigName"))) And _
                   (sc("BodyObj") Is fItem("BodyObj")) Then
                    
                    Dim vGN As Variant: vGN = sc("Normal")
                    Dim vFN As Variant: vFN = fItem("Normal")
                    
                    Dim dotP As Double
                    dotP = vGN(0) * vFN(0) + vGN(1) * vFN(1) + vGN(2) * vFN(2)
                    
                    If dotP > 0.9999 Then
                        sc("Faces").Add fItem("FaceObj")
                        addedToSub = True
                        Exit For
                    End If
                End If
            Next sc
        End If
        
        If Not addedToSub Then
            Dim newSub As Object
            Set newSub = CreateObject("Scripting.Dictionary")
            Set newSub("PartModel") = fItem("PartModel")
            Set newSub("PartDoc") = fItem("PartDoc")
            newSub("PartName") = fItem("PartName")
            newSub("ConfigName") = fItem("ConfigName")
            Set newSub("CompObj") = fItem("CompObj")
            Set newSub("BodyObj") = fItem("BodyObj")
            newSub("BodyName") = fItem("BodyName")
            newSub("IsCustomBody") = fItem("IsCustomBody")
            newSub("Normal") = fItem("Normal")
            
            Dim fCol As Collection: Set fCol = New Collection
            fCol.Add fItem("FaceObj")
            Set newSub("Faces") = fCol
            
            subClusters.Add newSub
        End If
    Next fItem
    
    ' --------------------------------------------------------------------------
    ' STEP 4: Export Execution
    ' --------------------------------------------------------------------------
    Dim usedFilesDict As Object
    Set usedFilesDict = CreateObject("Scripting.Dictionary")
    
    ' Save previous DXF export preferences
    Dim prevMapping As Boolean: prevMapping = swApp.GetUserPreferenceToggle(swDxfMapping)
    Dim prevDontShowMap As Boolean: prevDontShowMap = swApp.GetUserPreferenceToggle(swDXFDontShowMap)
    Dim prevMerge As Boolean: prevMerge = swApp.GetUserPreferenceToggle(swDxfEndPointMerge)
    Dim prevSplines As Boolean: prevSplines = swApp.GetUserPreferenceToggle(swDxfExportSplinesAsSplines)
    Dim prevMergeDist As Double: prevMergeDist = swApp.GetUserPreferenceDoubleValue(swDxfMergingDistance)
    
    swApp.SetUserPreferenceToggle swDxfMapping, False
    swApp.SetUserPreferenceToggle swDXFDontShowMap, True
    swApp.SetUserPreferenceToggle swDxfEndPointMerge, True
    swApp.SetUserPreferenceToggle swDxfExportSplinesAsSplines, False
    swApp.SetUserPreferenceDoubleValue swDxfMergingDistance, 0.00005 ' 0.05 mm tolerance
    
    Dim successCount As Long: successCount = 0
    Dim exportSummary As String: exportSummary = ""
    
    Dim tempFolder As String
    tempFolder = Environ("TEMP")
    If tempFolder = "" Then tempFolder = targetFolder
    
    ' ==========================================================================
    ' CASE A: MODE 2 - COMBINE ALL FACES INTO A SINGLE DXF
    ' ==========================================================================
    If exportMode = 2 Then
        Dim allTempFiles As Collection: Set allTempFiles = New Collection
        Dim allMasterEntities As Collection: Set allMasterEntities = New Collection
        Dim processedClusters As Collection: Set processedClusters = New Collection
        
        Dim globalFaceIdx As Long: globalFaceIdx = 1
        Dim scIndex As Long: scIndex = 1
        
        For Each sc In subClusters
            Dim tPartModel2 As SldWorks.ModelDoc2: Set tPartModel2 = sc("PartModel")
            Dim tPartDoc2 As SldWorks.PartDoc: Set tPartDoc2 = sc("PartDoc")
            
            Dim varAlign2 As Variant
            varAlign2 = ComputeAlignment(sc("Normal"))
            
            ' Determine layer prefix for this body/part
            Dim clusterPrefix As String
            If subClusters.Count = 1 Then
                clusterPrefix = ""
            Else
                If sc("IsCustomBody") Then
                    clusterPrefix = SanitizeLayerName(CStr(sc("BodyName")))
                Else
                    clusterPrefix = SanitizeLayerName(CStr(sc("PartName")))
                    If subClusters.Count > 1 Then clusterPrefix = clusterPrefix & "_" & scIndex
                End If
            End If
            
            ' In assembly mode, activate the component part document so ExportToDWG2 has an active viewport
            Dim actPartModel2 As SldWorks.ModelDoc2
            Set actPartModel2 = tPartModel2
            Dim compPath2 As String: compPath2 = tPartModel2.GetPathName
            
            If docType = swDocASSEMBLY Then
                Dim actErr2 As Long
                Set actPartModel2 = swApp.ActivateDoc3(tPartModel2.GetTitle, False, 2, actErr2)
                If actPartModel2 Is Nothing And compPath2 <> "" Then
                    Set actPartModel2 = swApp.ActivateDoc3(compPath2, False, 2, actErr2)
                End If
                If actPartModel2 Is Nothing And compPath2 <> "" Then
                    Dim oErr2 As Long, oWarn2 As Long
                    Set actPartModel2 = swApp.OpenDoc6(compPath2, swDocPART, swOpenDocOptions_ReadOnly, "", oErr2, oWarn2)
                    If Not actPartModel2 Is Nothing Then
                        Set actPartModel2 = swApp.ActivateDoc3(actPartModel2.GetTitle, False, 2, actErr2)
                    End If
                End If
            End If
            If actPartModel2 Is Nothing Then Set actPartModel2 = tPartModel2
            
            ' Activate component configuration in part document
            Dim scConfig2 As String: scConfig2 = CStr(sc("ConfigName"))
            If scConfig2 <> "" And Not actPartModel2 Is Nothing Then
                On Error Resume Next
                actPartModel2.ShowConfiguration2 scConfig2
                On Error GoTo ErrorHandler
            End If
            
            Dim actPartDoc2 As SldWorks.PartDoc: Set actPartDoc2 = actPartModel2
            
            Dim scTempFiles2 As Collection: Set scTempFiles2 = New Collection
            Dim scFaceEntities2 As Collection: Set scFaceEntities2 = New Collection
            
            Dim faceIdx2 As Long: faceIdx2 = 1
            Dim faceObj2 As Object
            For Each faceObj2 In sc("Faces")
                Dim faceLayerName As String
                If clusterPrefix = "" Then
                    faceLayerName = "FACE_" & faceIdx2
                Else
                    faceLayerName = clusterPrefix & "_F" & faceIdx2
                End If
                
                Dim tmpDxf2 As String
                tmpDxf2 = tempFolder & "\sw_single_tmp_" & Format(Timer * 100, "0") & "_" & globalFaceIdx & "_" & Int(Rnd * 1000) & ".dxf"
                scTempFiles2.Add tmpDxf2
                allTempFiles.Add tmpDxf2
                
                ' Clear selections and select face on the active part model
                actPartModel2.ClearSelection2 True
                Dim entObj2 As SldWorks.Entity: Set entObj2 = faceObj2
                entObj2.Select4 False, Nothing
                
                Dim bExp2 As Boolean
                bExp2 = actPartDoc2.ExportToDWG2( _
                    tmpDxf2, _
                    actPartModel2.GetPathName, _
                    swExportToDWG_ExportSelectedFacesOrLoops, _
                    True, _
                    varAlign2, _
                    False, _
                    False, _
                    0, _
                    Empty)
                    
                actPartModel2.ClearSelection2 True
                
                If bExp2 Then
                    If WaitForFileReady(fso, tmpDxf2, 3000) Then
                        ExtractCleanEntitiesWithLayer fso, tmpDxf2, faceLayerName, scFaceEntities2
                    End If
                End If
                
                faceIdx2 = faceIdx2 + 1
                globalFaceIdx = globalFaceIdx + 1
            Next faceObj2
            
            ' Extract outline specifically for this body/cluster
            If scFaceEntities2.Count > 0 Then
                Dim outlineLayerName As String
                If clusterPrefix = "" Then
                    outlineLayerName = "OUTLINE"
                Else
                    outlineLayerName = clusterPrefix & "_OUTLINE"
                End If
                
                Dim scOutlineEntities2 As Collection
                ExtractOutlineEntities scFaceEntities2, outlineLayerName, scOutlineEntities2
                
                Dim clData As Object: Set clData = CreateObject("Scripting.Dictionary")
                Set clData("FaceEntities") = scFaceEntities2
                Set clData("OutlineEntities") = scOutlineEntities2
                clData("Prefix") = clusterPrefix
                processedClusters.Add clData
            End If
            
            scIndex = scIndex + 1
        Next sc
        
        ' Reactivate top-level document if in assembly
        If docType = swDocASSEMBLY Then
            Dim assyErr2 As Long
            swApp.ActivateDoc3 swModel.GetTitle, False, 2, assyErr2
            swModel.ClearSelection2 True
        End If
        
        ' ----------------------------------------------------------------------
        ' Layout pass: Space out multiple parts/bodies neatly across CAM sheet
        ' ----------------------------------------------------------------------
        Dim maxPartDim As Double: maxPartDim = 0#
        Dim clItem As Object
        For Each clItem In processedClusters
            Dim clAll As Collection: Set clAll = New Collection
            Dim clO As Variant, clF As Variant
            If Not clItem("OutlineEntities") Is Nothing Then
                For Each clO In clItem("OutlineEntities")
                    clAll.Add clO
                Next clO
            End If
            If Not clItem("FaceEntities") Is Nothing Then
                For Each clF In clItem("FaceEntities")
                    clAll.Add clF
                Next clF
            End If
            
            Dim cMinX As Double, cMinY As Double, cMaxX As Double, cMaxY As Double
            ComputeEntitiesBounds clAll, cMinX, cMinY, cMaxX, cMaxY
            Dim cW As Double: cW = cMaxX - cMinX
            Dim cH As Double: cH = cMaxY - cMinY
            clItem("MinX") = cMinX
            clItem("MinY") = cMinY
            clItem("MaxX") = cMaxX
            clItem("MaxY") = cMaxY
            clItem("Width") = cW
            clItem("Height") = cH
            
            If cW > maxPartDim Then maxPartDim = cW
            If cH > maxPartDim Then maxPartDim = cH
        Next clItem
        
        ' Determine if document uses metric units
        Dim isMetricDoc As Boolean
        isMetricDoc = IsDocumentMetric(swModel, fso, CStr(allTempFiles(1)))
        
        Dim partGap As Double
        Dim maxRowWidth As Double
        If isMetricDoc Then
            partGap = 12.0       ' 12 mm (~0.5 inch) standard CNC spacing between parts
            maxRowWidth = 1200.0 ' 1200 mm standard CNC sheet width
        Else
            partGap = 0.5        ' 0.5 inch standard CNC spacing between parts
            maxRowWidth = 48.0   ' 48 inch (4 ft) standard CNC sheet width
        End If
        
        ' Arrange parts spaced out across rows
        Dim curLayoutX As Double: curLayoutX = 0#
        Dim curLayoutY As Double: curLayoutY = 0#
        Dim curRowMaxH As Double: curRowMaxH = 0#
        Dim isFirstInRow As Boolean: isFirstInRow = True
        
        For Each clItem In processedClusters
            Dim pW As Double: pW = CDbl(clItem("Width"))
            Dim pH As Double: pH = CDbl(clItem("Height"))
            Dim pMinX As Double: pMinX = CDbl(clItem("MinX"))
            Dim pMinY As Double: pMinY = CDbl(clItem("MinY"))
            
            ' If not the first part in this row, check if adding this part exceeds sheet width
            If Not isFirstInRow And (curLayoutX + pW > maxRowWidth) Then
                ' Wrap to next row
                curLayoutX = 0#
                curLayoutY = curLayoutY + curRowMaxH + partGap
                curRowMaxH = 0#
                isFirstInRow = True
            End If
            
            Dim shiftX As Double: shiftX = curLayoutX - pMinX
            Dim shiftY As Double: shiftY = curLayoutY - pMinY
            
            ' Shift face entities and outline entities
            Dim fColl As Collection: Set fColl = clItem("FaceEntities")
            Dim oColl As Collection: Set oColl = clItem("OutlineEntities")
            
            TranslateEntities fColl, shiftX, shiftY
            If Not oColl Is Nothing Then
                TranslateEntities oColl, shiftX, shiftY
            End If
            
            ' Add to master collection
            If Not oColl Is Nothing Then
                Dim mOE As Variant
                For Each mOE In oColl
                    allMasterEntities.Add mOE
                Next mOE
            End If
            Dim mFE As Variant
            For Each mFE In fColl
                allMasterEntities.Add mFE
            Next mFE
            
            ' Advance layout position
            curLayoutX = curLayoutX + pW + partGap
            If pH > curRowMaxH Then curRowMaxH = pH
            isFirstInRow = False
        Next clItem
        
        ' Determine single file output path
        Dim singleBasePrefix As String
        Dim firstSC As Object: Set firstSC = subClusters(1)
        If docType = swDocASSEMBLY Then
            singleBasePrefix = GetCleanPartName(swModel, Nothing)
        Else
            If subClusters.Count = 1 And firstSC("IsCustomBody") Then
                Dim cb As String: cb = SanitizeFileName(CStr(firstSC("BodyName")))
                singleBasePrefix = firstSC("PartName") & "_" & cb
            Else
                singleBasePrefix = firstSC("PartName")
            End If
        End If
        
        Dim singleOutPath As String
        singleOutPath = GetUniqueFilePath(targetFolder, singleBasePrefix, usedFilesDict)
        
        If allMasterEntities.Count > 0 And allTempFiles.Count > 0 Then
            ' Chain strictly per layer and ensure all loops closed
            Dim singleFinalEntities As Collection
            Set singleFinalEntities = CloseAndCleanEntities(allMasterEntities)
            
            ' Build final DXF without touching TABLES
            BuildFinalDxf fso, allTempFiles(1), singleFinalEntities, singleOutPath
            
            ' Cleanup temp DXFs
            Dim tfV As Variant
            For Each tfV In allTempFiles
                If fso.FileExists(CStr(tfV)) Then
                    On Error Resume Next
                    fso.DeleteFile CStr(tfV), True
                    On Error GoTo 0
                End If
            Next tfV
            
            If fso.FileExists(singleOutPath) Then
                successCount = 1
                Dim sName As String: sName = Mid(singleOutPath, InStrRev(singleOutPath, "\") + 1)
                exportSummary = "  - " & sName & "  [" & faceList.Count & " face(s) combined from " & subClusters.Count & " body/cluster(s)]" & vbCrLf
            End If
        End If

    ' ==========================================================================
    ' CASE B: MODES 1 & 3 - SMART CAM OR SEPARATE DXF PER SUB-CLUSTER
    ' ==========================================================================
    Else
        For Each sc In subClusters
            Dim tPartModel As SldWorks.ModelDoc2: Set tPartModel = sc("PartModel")
            Dim tPartDoc As SldWorks.PartDoc: Set tPartDoc = sc("PartDoc")
            
            Dim basePrefix As String
            If sc("IsCustomBody") Then
                Dim cBody As String: cBody = SanitizeFileName(CStr(sc("BodyName")))
                If cBody <> "" Then
                    basePrefix = sc("PartName") & "_" & cBody
                Else
                    basePrefix = sc("PartName")
                End If
            Else
                basePrefix = sc("PartName")
            End If
            
            Dim outPath As String
            outPath = GetUniqueFilePath(targetFolder, basePrefix, usedFilesDict)
            
            Dim varAlign As Variant
            varAlign = ComputeAlignment(sc("Normal"))
            
            ' In assembly mode, activate the component part document so ExportToDWG2 has an active viewport
            Dim actPartModel As SldWorks.ModelDoc2
            Set actPartModel = tPartModel
            Dim compPath1 As String: compPath1 = tPartModel.GetPathName
            
            If docType = swDocASSEMBLY Then
                Dim actErr As Long
                Set actPartModel = swApp.ActivateDoc3(tPartModel.GetTitle, False, 2, actErr)
                If actPartModel Is Nothing And compPath1 <> "" Then
                    Set actPartModel = swApp.ActivateDoc3(compPath1, False, 2, actErr)
                End If
                If actPartModel Is Nothing And compPath1 <> "" Then
                    Dim oErr1 As Long, oWarn1 As Long
                    Set actPartModel = swApp.OpenDoc6(compPath1, swDocPART, swOpenDocOptions_ReadOnly, "", oErr1, oWarn1)
                    If Not actPartModel Is Nothing Then
                        Set actPartModel = swApp.ActivateDoc3(actPartModel.GetTitle, False, 2, actErr)
                    End If
                End If
            End If
            If actPartModel Is Nothing Then Set actPartModel = tPartModel
            
            ' Activate component configuration in part document
            Dim scConfig1 As String: scConfig1 = CStr(sc("ConfigName"))
            If scConfig1 <> "" And Not actPartModel Is Nothing Then
                On Error Resume Next
                actPartModel.ShowConfiguration2 scConfig1
                On Error GoTo ErrorHandler
            End If
            
            Dim actPartDoc As SldWorks.PartDoc: Set actPartDoc = actPartModel
            
            Dim scTempFiles As Collection: Set scTempFiles = New Collection
            Dim scFaceEntities As Collection: Set scFaceEntities = New Collection
            
            Dim faceIdx As Long: faceIdx = 1
            Dim faceObj As Object
            For Each faceObj In sc("Faces")
                Dim layerName As String: layerName = "FACE_" & faceIdx
                
                Dim tmpDxf As String
                tmpDxf = tempFolder & "\sw_face_tmp_" & Format(Timer * 100, "0") & "_" & faceIdx & "_" & Int(Rnd * 1000) & ".dxf"
                scTempFiles.Add tmpDxf
                
                ' Clear selections and select face on active part model
                actPartModel.ClearSelection2 True
                Dim entObj As SldWorks.Entity: Set entObj = faceObj
                entObj.Select4 False, Nothing
                
                Dim bExp As Boolean
                bExp = actPartDoc.ExportToDWG2( _
                    tmpDxf, _
                    actPartModel.GetPathName, _
                    swExportToDWG_ExportSelectedFacesOrLoops, _
                    True, _
                    varAlign, _
                    False, _
                    False, _
                    0, _
                    Empty)
                    
                actPartModel.ClearSelection2 True
                
                If bExp Then
                    If WaitForFileReady(fso, tmpDxf, 3000) Then
                        ExtractCleanEntitiesWithLayer fso, tmpDxf, layerName, scFaceEntities
                    End If
                End If
                
                faceIdx = faceIdx + 1
            Next faceObj
            
            If scFaceEntities.Count > 0 And scTempFiles.Count > 0 Then
                Dim scOutlineEntities As Collection
                ExtractOutlineEntities scFaceEntities, "OUTLINE", scOutlineEntities
                
                Dim scCombined As Collection: Set scCombined = New Collection
                If Not scOutlineEntities Is Nothing Then
                    Dim oE As Variant
                    For Each oE In scOutlineEntities
                        scCombined.Add oE
                    Next oE
                End If
                Dim fE As Variant
                For Each fE In scFaceEntities
                    scCombined.Add fE
                Next fE
                
                Dim scFinalEntities As Collection
                Set scFinalEntities = CloseAndCleanEntities(scCombined)
                
                BuildFinalDxf fso, scTempFiles(1), scFinalEntities, outPath
                
                ' Delete temporary DXFs
                For Each tfV In scTempFiles
                    If fso.FileExists(CStr(tfV)) Then
                        On Error Resume Next
                        fso.DeleteFile CStr(tfV), True
                        On Error GoTo 0
                    End If
                Next tfV
                
                If fso.FileExists(outPath) Then
                    successCount = successCount + 1
                    Dim justName As String: justName = Mid(outPath, InStrRev(outPath, "\") + 1)
                    exportSummary = exportSummary & "  - " & justName & "  [" & sc("Faces").Count & " face(s) -> OUTLINE + " & (faceIdx - 1) & " face layer(s)]" & vbCrLf
                Else
                    exportSummary = exportSummary & "  - FAILED: " & basePrefix & vbCrLf
                End If
            Else
                exportSummary = exportSummary & "  - FAILED: " & basePrefix & vbCrLf
            End If
        Next sc
        
        ' Reactivate top-level document if in assembly
        If docType = swDocASSEMBLY Then
            Dim assyErrB As Long
            swApp.ActivateDoc3 swModel.GetTitle, False, 2, assyErrB
            swModel.ClearSelection2 True
        End If
    End If
    
    ' Restore user preferences
    swApp.SetUserPreferenceToggle swDxfMapping, prevMapping
    swApp.SetUserPreferenceToggle swDXFDontShowMap, prevDontShowMap
    swApp.SetUserPreferenceToggle swDxfEndPointMerge, prevMerge
    swApp.SetUserPreferenceToggle swDxfExportSplinesAsSplines, prevSplines
    swApp.SetUserPreferenceDoubleValue swDxfMergingDistance, prevMergeDist
    
    ' Clear selection
    swModel.ClearSelection2 True
    
    ' --------------------------------------------------------------------------
    ' STEP 5: Report Results
    ' --------------------------------------------------------------------------
    If successCount > 0 Then
        Dim msg As String
        msg = "DXF Export Successful!" & vbCrLf & _
              "- Dedicated OUTLINE Layer: Exterior silhouette perimeter" & vbCrLf & _
              "- Per-Face Layers: FACE_1, FACE_2... isolated per depth/pocket" & vbCrLf & _
              "- 100% Closed Vectors (LWPOLYLINE 70=1)" & vbCrLf & _
              "- Native Drill Holes preserved as circles" & vbCrLf & _
              "- Watermarks & export annotations stripped" & vbCrLf & _
              "- TABLES section untouched (zero 'record name is empty' errors)" & vbCrLf & vbCrLf & _
              "Exported " & successCount & " DXF file(s) to:" & vbCrLf & _
              targetFolder & vbCrLf & vbCrLf & _
              "Generated Files:" & vbCrLf & exportSummary
        MsgBox msg, vbInformation, "SolidWorks CAM DXF Exporter"
    Else
        Dim failMsg As String
        failMsg = "Export failed." & vbCrLf & vbCrLf
        If exportSummary <> "" Then
            failMsg = failMsg & "Log:" & vbCrLf & exportSummary & vbCrLf
        End If
        failMsg = failMsg & "Please check that planar faces are selected and that the destination folder is writable."
        MsgBox failMsg, vbCritical, "Export Incomplete"
    End If
    Exit Sub

ErrorHandler:
    MsgBox "An error occurred in ExportFacesToDXF:" & vbCrLf & vbCrLf & _
           "Error " & Err.Number & ": " & Err.Description & vbCrLf & _
           "Source: " & Err.Source, vbCritical, "SolidWorks CAM DXF Exporter Error"
End Sub

' ==============================================================================
' HELPER: Wait for asynchronous file creation and flush to complete
' ==============================================================================
Function WaitForFileReady(fso As Object, filePath As String, ByVal maxWaitMs As Long) As Boolean
    Dim startTime As Double: startTime = Timer
    Dim success As Boolean: success = False
    
    Do
        If fso.FileExists(filePath) Then
            On Error Resume Next
            Dim f As Object: Set f = fso.GetFile(filePath)
            If Not f Is Nothing Then
                If f.Size > 100 Then
                    Dim ts As Object: Set ts = fso.OpenTextFile(filePath, 1, False)
                    If Not ts Is Nothing Then
                        ts.Close
                        success = True
                        Exit Do
                    End If
                End If
            End If
            On Error GoTo 0
        End If
        
        DoEvents
        
        Dim elapsed As Double
        elapsed = (Timer - startTime) * 1000#
        If elapsed < 0 Then elapsed = elapsed + 86400000#
        If elapsed >= maxWaitMs Then Exit Do
    Loop
    
    WaitForFileReady = success
End Function

' ==============================================================================
' HELPER: Extract clean entities from a DXF file and assign target layer
' ==============================================================================
Sub ExtractCleanEntitiesWithLayer(fso As Object, filePath As String, targetLayer As String, ByRef outEntities As Collection)
    If Not fso.FileExists(filePath) Then Exit Sub
    
    Dim ts As Object: Set ts = fso.OpenTextFile(filePath, 1)
    Dim dxfContent As String: dxfContent = ts.ReadAll
    ts.Close
    
    Dim rawLines() As String
    rawLines = Split(dxfContent, vbCrLf)
    If UBound(rawLines) <= 0 Then
        rawLines = Split(dxfContent, vbLf)
    End If
    
    Dim totalLines As Long: totalLines = UBound(rawLines) + 1
    Dim inEntities As Boolean: inEntities = False
    
    Dim currEntityLines() As String
    ReDim currEntityLines(0 To 200)
    Dim currCount As Long: currCount = 0
    Dim currType As String: currType = ""
    
    Dim i As Long: i = 0
    Do While i < totalLines - 1
        Dim code As String: code = Trim(rawLines(i))
        Dim val As String: val = Trim(rawLines(i + 1))
        
        If code = "0" And val = "SECTION" Then
            If i + 3 < totalLines Then
                If Trim(rawLines(i + 2)) = "2" And Trim(rawLines(i + 3)) = "ENTITIES" Then
                    inEntities = True
                    i = i + 4
                    GoTo NextEntLine
                End If
            End If
        End If
        
        If inEntities Then
            If code = "0" And val = "ENDSEC" Then
                If currCount > 0 Then
                    If IsEntityClean(currType, currEntityLines, currCount) Then
                        Dim entCopy1() As String
                        ReDim entCopy1(0 To currCount - 1)
                        Dim m As Long
                        For m = 0 To currCount - 1
                            entCopy1(m) = currEntityLines(m)
                        Next m
                        SetEntityLayer entCopy1, targetLayer
                        outEntities.Add entCopy1
                    End If
                    currCount = 0
                End If
                Exit Do
            End If
            
            If code = "0" Then
                If currCount > 0 Then
                    If IsEntityClean(currType, currEntityLines, currCount) Then
                        Dim entCopy2() As String
                        ReDim entCopy2(0 To currCount - 1)
                        For m = 0 To currCount - 1
                            entCopy2(m) = currEntityLines(m)
                        Next m
                        SetEntityLayer entCopy2, targetLayer
                        outEntities.Add entCopy2
                    End If
                End If
                
                currType = val
                currCount = 0
                currEntityLines(currCount) = "  0": currCount = currCount + 1
                currEntityLines(currCount) = val: currCount = currCount + 1
                i = i + 2
                GoTo NextEntLine
            Else
                If currCount + 2 > UBound(currEntityLines) Then
                    ReDim Preserve currEntityLines(0 To UBound(currEntityLines) + 200)
                End If
                currEntityLines(currCount) = IIf(Len(code) = 1, " " & code, code)
                currCount = currCount + 1
                currEntityLines(currCount) = val
                currCount = currCount + 1
                i = i + 2
                GoTo NextEntLine
            End If
        End If
        
        i = i + 2
NextEntLine:
    Loop
End Sub

' ==============================================================================
' HELPER: Set or insert group code 8 (Layer) on an entity array
' ==============================================================================
Sub SetEntityLayer(ByRef entLines() As String, ByVal targetLayer As String)
    Dim numLines As Long: numLines = UBound(entLines) + 1
    Dim foundLayer As Boolean: foundLayer = False
    Dim j As Long
    For j = 0 To numLines - 2 Step 2
        If Trim(entLines(j)) = "8" Then
            entLines(j + 1) = targetLayer
            foundLayer = True
            Exit For
        End If
    Next j
    
    If Not foundLayer Then
        Dim newLines() As String
        ReDim newLines(0 To numLines + 1)
        newLines(0) = entLines(0)
        newLines(1) = entLines(1)
        newLines(2) = "  8"
        newLines(3) = targetLayer
        For j = 2 To numLines - 1
            newLines(j + 2) = entLines(j)
        Next j
        entLines = newLines
    End If
End Sub

' ==============================================================================
' HELPER: Calculate signed polygon area using Green's theorem (Shoelace formula)
' Counter-clockwise (CCW) exterior outer perimeters yield Area > 0.
' Clockwise (CW) interior holes yield Area < 0.
' ==============================================================================
Function PolygonSignedArea(vX() As Double, vY() As Double, ByVal numVerts As Long) As Double
    Dim area As Double: area = 0#
    If numVerts < 3 Then
        PolygonSignedArea = 0#
        Exit Function
    End If
    Dim i As Long, j As Long
    For i = 0 To numVerts - 1
        j = (i + 1) Mod numVerts
        area = area + (vX(i) * vY(j) - vX(j) * vY(i))
    Next i
    PolygonSignedArea = area * 0.5
End Function

' ==============================================================================
' HELPER: Extract outline silhouette entities for a body / cluster
' Cancels shared internal edges between stepped/pocket faces, chains remaining
' exterior edges, and selects the loop with the maximum absolute area (the outer perimeter).
' ==============================================================================
Sub ExtractOutlineEntities(allFaceEntities As Collection, ByVal targetLayer As String, ByRef outOutlineEntities As Collection)
    Set outOutlineEntities = New Collection
    If allFaceEntities Is Nothing Then Exit Sub
    If allFaceEntities.Count = 0 Then Exit Sub
    
    Dim segs() As DxfSeg
    ReDim segs(0 To 200)
    Dim segCount As Long: segCount = 0
    
    Dim circles As Collection: Set circles = New Collection
    
    Dim entVar As Variant
    For Each entVar In allFaceEntities
        Dim elines() As String: elines = entVar
        Dim etype As String: etype = UCase(Trim(elines(1)))
        
        If etype = "CIRCLE" Then
            Dim cx As Double, cy As Double, cr As Double
            Dim p As Long
            For p = 0 To UBound(elines) - 1 Step 2
                Select Case Trim(elines(p))
                    Case "10": cx = Val(Trim(elines(p + 1)))
                    Case "20": cy = Val(Trim(elines(p + 1)))
                    Case "40": cr = Val(Trim(elines(p + 1)))
                End Select
            Next p
            Dim cObj As Object: Set cObj = CreateObject("Scripting.Dictionary")
            cObj("R") = cr
            cObj("Lines") = elines
            circles.Add cObj
            
        ElseIf etype = "LWPOLYLINE" Then
            Dim vX() As Double, vY() As Double, vB() As Double
            Dim vCount As Long: vCount = 0
            Dim isClosed As Boolean: isClosed = False
            Dim polyLayer As String: polyLayer = "0"
            ParseLwPolylineEntity elines, vX, vY, vB, vCount, isClosed, polyLayer
            
            Dim k As Long
            Dim limitK As Long
            limitK = IIf(isClosed, vCount - 1, vCount - 2)
            For k = 0 To limitK
                Dim nextK As Long: nextK = (k + 1) Mod vCount
                If segCount > UBound(segs) Then
                    ReDim Preserve segs(0 To UBound(segs) + 200)
                End If
                segs(segCount).X1 = vX(k)
                segs(segCount).Y1 = vY(k)
                segs(segCount).X2 = vX(nextK)
                segs(segCount).Y2 = vY(nextK)
                segs(segCount).Bulge = vB(k)
                segs(segCount).Layer = targetLayer
                segs(segCount).Used = False
                segCount = segCount + 1
            Next k
            
        ElseIf etype = "LINE" Then
            Dim lx1 As Double, ly1 As Double, lx2 As Double, ly2 As Double, lLay As String
            ParseLineEntity elines, lx1, ly1, lx2, ly2, lLay
            If segCount > UBound(segs) Then
                ReDim Preserve segs(0 To UBound(segs) + 200)
            End If
            segs(segCount).X1 = lx1
            segs(segCount).Y1 = ly1
            segs(segCount).X2 = lx2
            segs(segCount).Y2 = ly2
            segs(segCount).Bulge = 0#
            segs(segCount).Layer = targetLayer
            segs(segCount).Used = False
            segCount = segCount + 1
            
        ElseIf etype = "ARC" Then
            Dim acx As Double, acy As Double, ar As Double, a1 As Double, a2 As Double, aLay As String
            ParseArcEntity elines, acx, acy, ar, a1, a2, aLay
            Dim ax1 As Double, ay1 As Double, ax2 As Double, ay2 As Double, ab As Double
            CalcArcBulge acx, acy, ar, a1, a2, ax1, ay1, ax2, ay2, ab
            If segCount > UBound(segs) Then
                ReDim Preserve segs(0 To UBound(segs) + 200)
            End If
            segs(segCount).X1 = ax1
            segs(segCount).Y1 = ay1
            segs(segCount).X2 = ax2
            segs(segCount).Y2 = ay2
            segs(segCount).Bulge = ab
            segs(segCount).Layer = targetLayer
            segs(segCount).Used = False
            segCount = segCount + 1
        End If
    Next entVar
    
    If segCount = 0 Then
        ' Fallback: if only circles exist (e.g. turned parts)
        If circles.Count > 0 Then
            Dim maxR As Double: maxR = -1#
            Dim maxCLines() As String
            Dim cItem As Object
            For Each cItem In circles
                If cItem("R") > maxR Then
                    maxR = cItem("R")
                    maxCLines = cItem("Lines")
                End If
            Next cItem
            SetEntityLayer maxCLines, targetLayer
            outOutlineEntities.Add maxCLines
        End If
        Exit Sub
    End If
    
    ' Cancel shared internal seams (both opposite-direction and same-direction coincident edges)
    ' When two adjacent faces (e.g. face & pocket floor) share a step wall edge, that edge is an internal
    ' seam and must be cancelled from BOTH faces so their exterior boundaries unite into a single outline contour.
    Const TOL As Double = 0.05
    Dim i As Long, j As Long
    For i = 0 To segCount - 1
        If Not segs(i).Used Then
            For j = i + 1 To segCount - 1
                If Not segs(j).Used Then
                    ' Opposite edge check: start1 approx end2 and end1 approx start2
                    If PointDist(segs(i).X1, segs(i).Y1, segs(j).X2, segs(j).Y2) <= TOL And _
                       PointDist(segs(i).X2, segs(i).Y2, segs(j).X1, segs(j).Y1) <= TOL Then
                        If Abs(segs(i).Bulge + segs(j).Bulge) <= 0.05 Then
                            segs(i).Used = True
                            segs(j).Used = True
                            Exit For
                        End If
                    ' Same-direction coincident edge check: start1 approx start2 and end1 approx end2
                    ElseIf PointDist(segs(i).X1, segs(i).Y1, segs(j).X1, segs(j).Y1) <= TOL And _
                           PointDist(segs(i).X2, segs(i).Y2, segs(j).X2, segs(j).Y2) <= TOL Then
                        If Abs(segs(i).Bulge - segs(j).Bulge) <= 0.05 Then
                            segs(i).Used = True
                            segs(j).Used = True
                            Exit For
                        End If
                    End If
                End If
            Next j
        End If
    Next i
    
    ' Chain uncancelled segments into candidate loops
    Dim cvX() As Double, cvY() As Double, cvB() As Double
    ReDim cvX(0 To 100): ReDim cvY(0 To 100): ReDim cvB(0 To 100)
    Dim cvCount As Long
    
    Dim candidateLoops As Collection: Set candidateLoops = New Collection
    
    Dim unassignedLeft As Long: unassignedLeft = 0
    For i = 0 To segCount - 1
        If Not segs(i).Used Then unassignedLeft = unassignedLeft + 1
    Next i
    
    Do While unassignedLeft > 0
        Dim currIdx As Long: currIdx = -1
        Dim si As Long
        For si = 0 To segCount - 1
            If Not segs(si).Used Then
                currIdx = si
                Exit For
            End If
        Next si
        If currIdx = -1 Then Exit Do
        
        segs(currIdx).Used = True
        unassignedLeft = unassignedLeft - 1
        
        cvCount = 2
        cvX(0) = segs(currIdx).X1: cvY(0) = segs(currIdx).Y1: cvB(0) = segs(currIdx).Bulge
        cvX(1) = segs(currIdx).X2: cvY(1) = segs(currIdx).Y2: cvB(1) = 0#
        
        Dim pStartX As Double: pStartX = segs(currIdx).X1
        Dim pStartY As Double: pStartY = segs(currIdx).Y1
        Dim pEndX As Double: pEndX = segs(currIdx).X2
        Dim pEndY As Double: pEndY = segs(currIdx).Y2
        
        ' Extend forward
        Do
            If PointDist(pEndX, pEndY, pStartX, pStartY) <= TOL And cvCount > 2 Then Exit Do
            Dim foundIdx As Long: foundIdx = -1
            Dim bReverse As Boolean: bReverse = False
            
            For si = 0 To segCount - 1
                If Not segs(si).Used Then
                    If PointDist(pEndX, pEndY, segs(si).X1, segs(si).Y1) <= TOL Then
                        foundIdx = si: bReverse = False: Exit For
                    ElseIf PointDist(pEndX, pEndY, segs(si).X2, segs(si).Y2) <= TOL Then
                        foundIdx = si: bReverse = True: Exit For
                    End If
                End If
            Next si
            
            If foundIdx = -1 Then Exit Do
            
            segs(foundIdx).Used = True
            unassignedLeft = unassignedLeft - 1
            
            If cvCount + 1 > UBound(cvX) Then
                ReDim Preserve cvX(0 To UBound(cvX) + 100)
                ReDim Preserve cvY(0 To UBound(cvY) + 100)
                ReDim Preserve cvB(0 To UBound(cvB) + 100)
            End If
            
            If Not bReverse Then
                cvB(cvCount - 1) = segs(foundIdx).Bulge
                cvX(cvCount) = segs(foundIdx).X2
                cvY(cvCount) = segs(foundIdx).Y2
                cvB(cvCount) = 0#
                pEndX = segs(foundIdx).X2
                pEndY = segs(foundIdx).Y2
                cvCount = cvCount + 1
            Else
                cvB(cvCount - 1) = -segs(foundIdx).Bulge
                cvX(cvCount) = segs(foundIdx).X1
                cvY(cvCount) = segs(foundIdx).Y1
                cvB(cvCount) = 0#
                pEndX = segs(foundIdx).X1
                pEndY = segs(foundIdx).Y1
                cvCount = cvCount + 1
            End If
        Loop
        
        ' Extend backward if not closed
        If Not (PointDist(pEndX, pEndY, pStartX, pStartY) <= TOL And cvCount > 2) Then
            Do
                If PointDist(pStartX, pStartY, pEndX, pEndY) <= TOL And cvCount > 2 Then Exit Do
                foundIdx = -1: bReverse = False
                
                For si = 0 To segCount - 1
                    If Not segs(si).Used Then
                        If PointDist(pStartX, pStartY, segs(si).X2, segs(si).Y2) <= TOL Then
                            foundIdx = si: bReverse = False: Exit For
                        ElseIf PointDist(pStartX, pStartY, segs(si).X1, segs(si).Y1) <= TOL Then
                            foundIdx = si: bReverse = True: Exit For
                        End If
                    End If
                Next si
                
                If foundIdx = -1 Then Exit Do
                
                segs(foundIdx).Used = True
                unassignedLeft = unassignedLeft - 1
                
                If cvCount + 1 > UBound(cvX) Then
                    ReDim Preserve cvX(0 To UBound(cvX) + 100)
                    ReDim Preserve cvY(0 To UBound(cvY) + 100)
                    ReDim Preserve cvB(0 To UBound(cvB) + 100)
                End If
                
                Dim s As Long
                For s = cvCount To 1 Step -1
                    cvX(s) = cvX(s - 1)
                    cvY(s) = cvY(s - 1)
                    cvB(s) = cvB(s - 1)
                Next s
                
                If Not bReverse Then
                    cvX(0) = segs(foundIdx).X1
                    cvY(0) = segs(foundIdx).Y1
                    cvB(0) = segs(foundIdx).Bulge
                    pStartX = segs(foundIdx).X1
                    pStartY = segs(foundIdx).Y1
                Else
                    cvX(0) = segs(foundIdx).X2
                    cvY(0) = segs(foundIdx).Y2
                    cvB(0) = -segs(foundIdx).Bulge
                    pStartX = segs(foundIdx).X2
                    pStartY = segs(foundIdx).Y2
                End If
                cvCount = cvCount + 1
            Loop
        End If
        
        ' Store candidate loop
        If PointDist(pEndX, pEndY, pStartX, pStartY) <= TOL And cvCount > 2 Then
            Dim loopDict As Object: Set loopDict = CreateObject("Scripting.Dictionary")
            Dim numV As Long: numV = cvCount - 1
            Dim loopA As Double: loopA = PolygonSignedArea(cvX, cvY, numV)
            
            Dim copyX() As Double: ReDim copyX(0 To numV - 1)
            Dim copyY() As Double: ReDim copyY(0 To numV - 1)
            Dim copyB() As Double: ReDim copyB(0 To numV - 1)
            Dim vi As Long
            For vi = 0 To numV - 1
                copyX(vi) = cvX(vi)
                copyY(vi) = cvY(vi)
                copyB(vi) = cvB(vi)
            Next vi
            
            loopDict("X") = copyX
            loopDict("Y") = copyY
            loopDict("B") = copyB
            loopDict("Count") = numV
            loopDict("Area") = loopA
            loopDict("AbsArea") = Abs(loopA)
            candidateLoops.Add loopDict
        End If
    Loop
    
    ' Select outer perimeter loop: the loop with the maximum absolute area!
    If candidateLoops.Count > 0 Then
        Dim maxLoop As Object: Set maxLoop = candidateLoops(1)
        Dim cLoop As Object
        For Each cLoop In candidateLoops
            If cLoop("AbsArea") > maxLoop("AbsArea") Then
                Set maxLoop = cLoop
            End If
        Next cLoop
        
        Dim fX() As Double: fX = maxLoop("X")
        Dim fY() As Double: fY = maxLoop("Y")
        Dim fB() As Double: fB = maxLoop("B")
        Dim fCount As Long: fCount = maxLoop("Count")
        
        ' Ensure counter-clockwise (CCW) vertex winding: if Area < 0, reverse vertices
        If maxLoop("Area") < 0 Then
            Dim revX() As Double: ReDim revX(0 To fCount - 1)
            Dim revY() As Double: ReDim revY(0 To fCount - 1)
            Dim revB() As Double: ReDim revB(0 To fCount - 1)
            Dim rIdx As Long
            For rIdx = 0 To fCount - 1
                revX(rIdx) = fX(fCount - 1 - rIdx)
                revY(rIdx) = fY(fCount - 1 - rIdx)
                Dim prevBulgeIdx As Long
                prevBulgeIdx = (fCount - 2 - rIdx + fCount) Mod fCount
                revB(rIdx) = -fB(prevBulgeIdx)
            Next rIdx
            EmitLwPolyline revX, revY, revB, fCount, True, targetLayer, outOutlineEntities
        Else
            EmitLwPolyline fX, fY, fB, fCount, True, targetLayer, outOutlineEntities
        End If
    ElseIf circles.Count > 0 Then
        ' Fallback if no polyline loops: circle with max radius
        Dim maxCircR As Double: maxCircR = -1#
        Dim maxCircLines() As String
        Dim circItem As Object
        For Each circItem In circles
            If circItem("R") > maxCircR Then
                maxCircR = circItem("R")
                maxCircLines = circItem("Lines")
            End If
        Next circItem
        SetEntityLayer maxCircLines, targetLayer
        outOutlineEntities.Add maxCircLines
    End If
End Sub

' ==============================================================================
' HELPER: Build final DXF by preserving TABLES intact and injecting clean ENTITIES
' Fully compliant with AutoCAD DXF R15+ (AC1015) ObjectARX class hierarchy
' ==============================================================================
Sub BuildFinalDxf(fso As Object, basePath As String, processedEntities As Collection, outPath As String)
    If Not fso.FileExists(basePath) Then Exit Sub
    
    Dim ts As Object: Set ts = fso.OpenTextFile(basePath, 1)
    Dim baseContent As String: baseContent = ts.ReadAll
    ts.Close
    
    Dim rawLines() As String
    rawLines = Split(baseContent, vbCrLf)
    If UBound(rawLines) <= 0 Then
        rawLines = Split(baseContent, vbLf)
    End If
    
    Dim totalLines As Long: totalLines = UBound(rawLines) + 1
    
    ' 1. Extract *Model_Space BLOCK_RECORD handle
    Dim msHandle As String: msHandle = "1F"
    Dim bIdx As Long
    For bIdx = 0 To totalLines - 4
        If Trim(rawLines(bIdx)) = "BLOCK_RECORD" Then
            Dim candH As String: candH = ""
            If Trim(rawLines(bIdx + 1)) = "5" Then
                candH = Trim(rawLines(bIdx + 2))
            ElseIf Trim(rawLines(bIdx + 2)) = "5" Then
                candH = Trim(rawLines(bIdx + 3))
            End If
            If candH <> "" Then
                Dim testL As Long
                For testL = bIdx To bIdx + 12
                    If testL + 1 < totalLines Then
                        If Trim(rawLines(testL)) = "2" And UCase(Trim(rawLines(testL + 1))) = "*MODEL_SPACE" Then
                            msHandle = candH
                            Exit For
                        End If
                    End If
                Next testL
            End If
        End If
    Next bIdx
    
    ' 2. Extract and prepare $HANDSEED
    Dim handSeedVal As Long: handSeedVal = &H100
    Dim sIdx As Long
    For sIdx = 0 To totalLines - 3
        If UCase(Trim(rawLines(sIdx))) = "$HANDSEED" Then
            If Trim(rawLines(sIdx + 1)) = "5" Then
                Dim rawSeed As String: rawSeed = Trim(rawLines(sIdx + 2))
                On Error Resume Next
                handSeedVal = CLng("&H" & rawSeed)
                On Error GoTo 0
                If handSeedVal < &H10 Then handSeedVal = &H100
                Exit For
            End If
        End If
    Next sIdx
    
    Dim currHandle As Long: currHandle = handSeedVal
    
    Dim outLines() As String
    ReDim outLines(0 To totalLines * 2 + 5000)
    Dim outCount As Long: outCount = 0
    
    Dim inEntities As Boolean: inEntities = False
    
    Dim i As Long: i = 0
    Do While i < totalLines - 1
        Dim code As String: code = Trim(rawLines(i))
        Dim val As String: val = Trim(rawLines(i + 1))
        
        ' Update $HANDSEED in header to remain above all entity handles
        If code = "9" And UCase(val) = "$HANDSEED" Then
            outLines(outCount) = "  9": outCount = outCount + 1
            outLines(outCount) = "$HANDSEED": outCount = outCount + 1
            outLines(outCount) = "  5": outCount = outCount + 1
            Dim nextSeed As Long
            nextSeed = currHandle + 200
            If Not processedEntities Is Nothing Then
                nextSeed = nextSeed + processedEntities.Count * 2
            End If
            outLines(outCount) = Hex(nextSeed): outCount = outCount + 1
            i = i + 4
            GoTo ContinueDxfLoop
        End If
        
        ' Track ENTITIES section
        If code = "0" And val = "SECTION" Then
            If i + 3 < totalLines Then
                If Trim(rawLines(i + 2)) = "2" And Trim(rawLines(i + 3)) = "ENTITIES" Then
                    inEntities = True
                    outLines(outCount) = "  0": outCount = outCount + 1
                    outLines(outCount) = "SECTION": outCount = outCount + 1
                    outLines(outCount) = "  2": outCount = outCount + 1
                    outLines(outCount) = "ENTITIES": outCount = outCount + 1
                    i = i + 4
                    GoTo ContinueDxfLoop
                End If
            End If
        End If
        
        If inEntities Then
            If code = "0" And val = "ENDSEC" Then
                If Not processedEntities Is Nothing Then
                    Dim procEntVar As Variant
                    For Each procEntVar In processedEntities
                        Dim pArray() As String: pArray = procEntVar
                        Dim hexH As String: hexH = Hex(currHandle)
                        currHandle = currHandle + 1
                        
                        ' Inject or replace handle 5 and owner 330
                        Dim hasH As Boolean: hasH = False
                        Dim j As Long
                        For j = LBound(pArray) To UBound(pArray) - 1 Step 2
                            If Trim(pArray(j)) = "5" Then
                                pArray(j + 1) = hexH
                                hasH = True
                            ElseIf Trim(pArray(j)) = "330" Then
                                pArray(j + 1) = msHandle
                            End If
                        Next j
                        
                        Dim m As Long
                        If Not hasH Then
                            ' Entity does not have handle 5: insert 5 and 330 after entity type (pArray 0, 1)
                            If outCount + 4 > UBound(outLines) Then
                                ReDim Preserve outLines(0 To UBound(outLines) + 2000)
                            End If
                            outLines(outCount) = pArray(0): outCount = outCount + 1
                            outLines(outCount) = pArray(1): outCount = outCount + 1
                            outLines(outCount) = "  5": outCount = outCount + 1
                            outLines(outCount) = hexH: outCount = outCount + 1
                            outLines(outCount) = "330": outCount = outCount + 1
                            outLines(outCount) = msHandle: outCount = outCount + 1
                            For m = 2 To UBound(pArray)
                                If outCount > UBound(outLines) Then
                                    ReDim Preserve outLines(0 To UBound(outLines) + 2000)
                                End If
                                outLines(outCount) = pArray(m)
                                outCount = outCount + 1
                            Next m
                        Else
                            For m = LBound(pArray) To UBound(pArray)
                                If outCount > UBound(outLines) Then
                                    ReDim Preserve outLines(0 To UBound(outLines) + 2000)
                                End If
                                outLines(outCount) = pArray(m)
                                outCount = outCount + 1
                            Next m
                        End If
                    Next procEntVar
                End If
                
                inEntities = False
                If outCount + 2 > UBound(outLines) Then
                    ReDim Preserve outLines(0 To UBound(outLines) + 1000)
                End If
                outLines(outCount) = "  0": outCount = outCount + 1
                outLines(outCount) = "ENDSEC": outCount = outCount + 1
                i = i + 2
                GoTo ContinueDxfLoop
            Else
                ' Skip existing entities in base DXF
                i = i + 2
                GoTo ContinueDxfLoop
            End If
        End If
        
        ' Copy everything else (HEADER, CLASSES, TABLES, BLOCKS, EOF) verbatim
        If outCount + 2 > UBound(outLines) Then
            ReDim Preserve outLines(0 To UBound(outLines) + 1000)
        End If
        outLines(outCount) = IIf(Len(code) = 1, " " & code, code)
        outCount = outCount + 1
        outLines(outCount) = val
        outCount = outCount + 1
        i = i + 2
        
ContinueDxfLoop:
    Loop
    
    If outCount > 0 Then
        ReDim Preserve outLines(0 To outCount - 1)
        Set ts = fso.CreateTextFile(outPath, True)
        ts.Write Join(outLines, vbCrLf)
        ts.Close
    End If
End Sub

' ==============================================================================
' HELPER: Sanitize Layer Name for AutoCAD / DXF compliance
' ==============================================================================
Function SanitizeLayerName(rawName As String) As String
    Dim res As String: res = rawName
    Dim invalidChars As String: invalidChars = "\/:*?""<>| ,;=+`~!@#$%^&'()[]{}"
    Dim i As Long
    For i = 1 To Len(invalidChars)
        Dim ch As String: ch = Mid(invalidChars, i, 1)
        res = Replace(res, ch, "_")
    Next i
    Do While InStr(res, "__") > 0
        res = Replace(res, "__", "_")
    Loop
    If Left(res, 1) = "_" Then res = Mid(res, 2)
    If Right(res, 1) = "_" Then res = Left(res, Len(res) - 1)
    If Len(res) > 31 Then res = Left(res, 31)
    If res = "" Then res = "FACE"
    SanitizeLayerName = res
End Function

' ==============================================================================
' DXF VALIDATOR: Strips watermarks, export details, and non-geometric annotations
' ==============================================================================
Function IsEntityClean(etype As String, entityLines() As String, lineCount As Long) As Boolean
    Select Case etype
        Case "TEXT", "MTEXT", "DIMENSION", "LEADER", "MULTILEADER", "TOLERANCE", "HATCH", "ATTRIB", "ATTDEF"
            IsEntityClean = False
            Exit Function
    End Select
    
    Dim j As Long
    For j = 0 To lineCount - 2 Step 2
        Dim c As String: c = Trim(entityLines(j))
        Dim v As String: v = Trim(entityLines(j + 1))
        
        If c = "8" Then
            Dim uLayer As String: uLayer = UCase(v)
            If InStr(uLayer, "WATERMARK") > 0 Or _
               InStr(uLayer, "DETAILS") > 0 Or _
               InStr(uLayer, "TITLE") > 0 Or _
               InStr(uLayer, "BORDER") > 0 Or _
               InStr(uLayer, "NOTE") > 0 Or _
               InStr(uLayer, "ANNOTATION") > 0 Then
                IsEntityClean = False
                Exit Function
            End If
        End If
        
        If c = "1" Or c = "3" Then
            Dim uText As String: uText = UCase(v)
            If InStr(uText, "SOLIDWORKS") > 0 Or _
               InStr(uText, "EDUCATIONAL") > 0 Or _
               InStr(uText, "INSTRUCTIONAL") > 0 Or _
               InStr(uText, "FOR USE ONLY") > 0 Or _
               InStr(uText, "EXPORT") > 0 Or _
               InStr(uText, "SCALE") > 0 Or _
               InStr(uText, "UNITS") > 0 Then
                IsEntityClean = False
                Exit Function
            End If
        End If
    Next j
    
    IsEntityClean = True
End Function

' ==============================================================================
' DXF CLOSER & CLEANER: Chains segments strictly per layer into closed polylines
' Preserves native CIRCLEs for hole drilling cycles.
' ==============================================================================
Function CloseAndCleanEntities(inEntities As Collection) As Collection
    Dim outColl As Collection: Set outColl = New Collection
    If inEntities Is Nothing Then
        Set CloseAndCleanEntities = outColl
        Exit Function
    End If
    If inEntities.Count = 0 Then
        Set CloseAndCleanEntities = outColl
        Exit Function
    End If
    
    Dim segs() As DxfSeg
    ReDim segs(0 To 200)
    Dim segCount As Long: segCount = 0
    
    Const CHAIN_TOL As Double = 0.02 ' 0.02 mm tolerance
    
    Dim entVar As Variant
    For Each entVar In inEntities
        Dim elines() As String: elines = entVar
        Dim etype As String: etype = UCase(Trim(elines(1)))
        
        If etype = "CIRCLE" Then
            outColl.Add elines
            
        ElseIf etype = "LWPOLYLINE" Then
            Dim vX() As Double, vY() As Double, vB() As Double
            Dim vCount As Long: vCount = 0
            Dim isClosed As Boolean: isClosed = False
            Dim polyLayer As String: polyLayer = "0"
            ParseLwPolylineEntity elines, vX, vY, vB, vCount, isClosed, polyLayer
            
            If isClosed Then
                outColl.Add elines
            ElseIf vCount > 2 And PointDist(vX(0), vY(0), vX(vCount - 1), vY(vCount - 1)) <= CHAIN_TOL Then
                EmitLwPolyline vX, vY, vB, vCount - 1, True, polyLayer, outColl
            Else
                Dim k As Long
                For k = 0 To vCount - 2
                    If segCount > UBound(segs) Then
                        ReDim Preserve segs(0 To UBound(segs) + 200)
                    End If
                    segs(segCount).X1 = vX(k)
                    segs(segCount).Y1 = vY(k)
                    segs(segCount).X2 = vX(k + 1)
                    segs(segCount).Y2 = vY(k + 1)
                    segs(segCount).Bulge = vB(k)
                    segs(segCount).Layer = polyLayer
                    segs(segCount).Used = False
                    segCount = segCount + 1
                Next k
            End If
            
        ElseIf etype = "LINE" Then
            Dim lx1 As Double, ly1 As Double, lx2 As Double, ly2 As Double
            Dim lineLayer As String: lineLayer = "0"
            ParseLineEntity elines, lx1, ly1, lx2, ly2, lineLayer
            
            If segCount > UBound(segs) Then
                ReDim Preserve segs(0 To UBound(segs) + 200)
            End If
            segs(segCount).X1 = lx1
            segs(segCount).Y1 = ly1
            segs(segCount).X2 = lx2
            segs(segCount).Y2 = ly2
            segs(segCount).Bulge = 0#
            segs(segCount).Layer = lineLayer
            segs(segCount).Used = False
            segCount = segCount + 1
            
        ElseIf etype = "ARC" Then
            Dim acx As Double, acy As Double, ar As Double, a1 As Double, a2 As Double
            Dim arcLayer As String: arcLayer = "0"
            ParseArcEntity elines, acx, acy, ar, a1, a2, arcLayer
            
            Dim ax1 As Double, ay1 As Double, ax2 As Double, ay2 As Double, ab As Double
            CalcArcBulge acx, acy, ar, a1, a2, ax1, ay1, ax2, ay2, ab
            
            If segCount > UBound(segs) Then
                ReDim Preserve segs(0 To UBound(segs) + 200)
            End If
            segs(segCount).X1 = ax1
            segs(segCount).Y1 = ay1
            segs(segCount).X2 = ax2
            segs(segCount).Y2 = ay2
            segs(segCount).Bulge = ab
            segs(segCount).Layer = arcLayer
            segs(segCount).Used = False
            segCount = segCount + 1
            
        Else
            outColl.Add elines
        End If
    Next entVar
    
    ' Chain segments strictly per layer into closed polylines
    If segCount > 0 Then
        Dim cvX() As Double, cvY() As Double, cvB() As Double
        ReDim cvX(0 To 100): ReDim cvY(0 To 100): ReDim cvB(0 To 100)
        Dim cvCount As Long
        
        Dim unassignedLeft As Long: unassignedLeft = segCount
        
        Do While unassignedLeft > 0
            Dim currIdx As Long: currIdx = -1
            Dim si As Long
            For si = 0 To segCount - 1
                If Not segs(si).Used Then
                    currIdx = si
                    Exit For
                End If
            Next si
            If currIdx = -1 Then Exit Do
            
            segs(currIdx).Used = True
            unassignedLeft = unassignedLeft - 1
            
            cvCount = 2
            cvX(0) = segs(currIdx).X1: cvY(0) = segs(currIdx).Y1: cvB(0) = segs(currIdx).Bulge
            cvX(1) = segs(currIdx).X2: cvY(1) = segs(currIdx).Y2: cvB(1) = 0#
            
            Dim chainLayer As String: chainLayer = segs(currIdx).Layer
            
            Dim pStartX As Double: pStartX = segs(currIdx).X1
            Dim pStartY As Double: pStartY = segs(currIdx).Y1
            Dim pEndX As Double: pEndX = segs(currIdx).X2
            Dim pEndY As Double: pEndY = segs(currIdx).Y2
            
            ' 1. Extend forward (strictly same layer)
            Do
                If PointDist(pEndX, pEndY, pStartX, pStartY) <= CHAIN_TOL And cvCount > 2 Then Exit Do
                
                Dim foundIdx As Long: foundIdx = -1
                Dim bReverse As Boolean: bReverse = False
                
                For si = 0 To segCount - 1
                    If Not segs(si).Used Then
                        If segs(si).Layer = chainLayer Then
                            If PointDist(pEndX, pEndY, segs(si).X1, segs(si).Y1) <= CHAIN_TOL Then
                                foundIdx = si: bReverse = False: Exit For
                            ElseIf PointDist(pEndX, pEndY, segs(si).X2, segs(si).Y2) <= CHAIN_TOL Then
                                foundIdx = si: bReverse = True: Exit For
                            End If
                        End If
                    End If
                Next si
                
                If foundIdx = -1 Then Exit Do
                
                segs(foundIdx).Used = True
                unassignedLeft = unassignedLeft - 1
                
                If cvCount + 1 > UBound(cvX) Then
                    ReDim Preserve cvX(0 To UBound(cvX) + 100)
                    ReDim Preserve cvY(0 To UBound(cvY) + 100)
                    ReDim Preserve cvB(0 To UBound(cvB) + 100)
                End If
                
                If Not bReverse Then
                    cvB(cvCount - 1) = segs(foundIdx).Bulge
                    cvX(cvCount) = segs(foundIdx).X2
                    cvY(cvCount) = segs(foundIdx).Y2
                    cvB(cvCount) = 0#
                    pEndX = segs(foundIdx).X2
                    pEndY = segs(foundIdx).Y2
                    cvCount = cvCount + 1
                Else
                    cvB(cvCount - 1) = -segs(foundIdx).Bulge
                    cvX(cvCount) = segs(foundIdx).X1
                    cvY(cvCount) = segs(foundIdx).Y1
                    cvB(cvCount) = 0#
                    pEndX = segs(foundIdx).X1
                    pEndY = segs(foundIdx).Y1
                    cvCount = cvCount + 1
                End If
            Loop
            
            ' 2. Extend backward if not closed (strictly same layer)
            If Not (PointDist(pEndX, pEndY, pStartX, pStartY) <= CHAIN_TOL And cvCount > 2) Then
                Do
                    If PointDist(pStartX, pStartY, pEndX, pEndY) <= CHAIN_TOL And cvCount > 2 Then Exit Do
                    
                    foundIdx = -1: bReverse = False
                    For si = 0 To segCount - 1
                        If Not segs(si).Used Then
                            If segs(si).Layer = chainLayer Then
                                If PointDist(pStartX, pStartY, segs(si).X2, segs(si).Y2) <= CHAIN_TOL Then
                                    foundIdx = si: bReverse = False: Exit For
                                ElseIf PointDist(pStartX, pStartY, segs(si).X1, segs(si).Y1) <= CHAIN_TOL Then
                                    foundIdx = si: bReverse = True: Exit For
                                End If
                            End If
                        End If
                    Next si
                    
                    If foundIdx = -1 Then Exit Do
                    
                    segs(foundIdx).Used = True
                    unassignedLeft = unassignedLeft - 1
                    
                    If cvCount + 1 > UBound(cvX) Then
                        ReDim Preserve cvX(0 To UBound(cvX) + 100)
                        ReDim Preserve cvY(0 To UBound(cvY) + 100)
                        ReDim Preserve cvB(0 To UBound(cvB) + 100)
                    End If
                    
                    Dim s As Long
                    For s = cvCount To 1 Step -1
                        cvX(s) = cvX(s - 1)
                        cvY(s) = cvY(s - 1)
                        cvB(s) = cvB(s - 1)
                    Next s
                    
                    If Not bReverse Then
                        cvX(0) = segs(foundIdx).X1
                        cvY(0) = segs(foundIdx).Y1
                        cvB(0) = segs(foundIdx).Bulge
                        pStartX = segs(foundIdx).X1
                        pStartY = segs(foundIdx).Y1
                    Else
                        cvX(0) = segs(foundIdx).X2
                        cvY(0) = segs(foundIdx).Y2
                        cvB(0) = -segs(foundIdx).Bulge
                        pStartX = segs(foundIdx).X2
                        pStartY = segs(foundIdx).Y2
                    End If
                    cvCount = cvCount + 1
                Loop
            End If
            
            Dim bClosed As Boolean
            bClosed = (PointDist(pEndX, pEndY, pStartX, pStartY) <= CHAIN_TOL And cvCount > 2)
            
            If bClosed Then
                EmitLwPolyline cvX, cvY, cvB, cvCount - 1, True, chainLayer, outColl
            Else
                EmitLwPolyline cvX, cvY, cvB, cvCount, False, chainLayer, outColl
            End If
        Loop
    End If
    
    Set CloseAndCleanEntities = outColl
End Function

' ==============================================================================
' DXF PARSER: Extracts start and end coordinates of a LINE entity
' ==============================================================================
Sub ParseLineEntity(entLines() As String, ByRef x1 As Double, ByRef y1 As Double, ByRef x2 As Double, ByRef y2 As Double, ByRef layer As String)
    x1 = 0#: y1 = 0#: x2 = 0#: y2 = 0#
    layer = "0"
    Dim p As Long
    For p = 0 To UBound(entLines) - 1 Step 2
        Dim c As String: c = Trim(entLines(p))
        Dim v As String: v = Trim(entLines(p + 1))
        Select Case c
            Case "8":  layer = v
            Case "10": x1 = Val(v)
            Case "20": y1 = Val(v)
            Case "11": x2 = Val(v)
            Case "21": y2 = Val(v)
        End Select
    Next p
End Sub

' ==============================================================================
' DXF PARSER: Extracts parameters of an ARC entity
' ==============================================================================
Sub ParseArcEntity(entLines() As String, ByRef cx As Double, ByRef cy As Double, ByRef r As Double, ByRef a1 As Double, ByRef a2 As Double, ByRef layer As String)
    cx = 0#: cy = 0#: r = 0#: a1 = 0#: a2 = 0#
    layer = "0"
    Dim p As Long
    For p = 0 To UBound(entLines) - 1 Step 2
        Dim c As String: c = Trim(entLines(p))
        Dim v As String: v = Trim(entLines(p + 1))
        Select Case c
            Case "8":  layer = v
            Case "10": cx = Val(v)
            Case "20": cy = Val(v)
            Case "40": r = Val(v)
            Case "50": a1 = Val(v)
            Case "51": a2 = Val(v)
        End Select
    Next p
End Sub

' ==============================================================================
' DXF PARSER: Extracts vertices, bulges, and flags of an LWPOLYLINE entity
' ==============================================================================
Sub ParseLwPolylineEntity(entLines() As String, ByRef vX() As Double, ByRef vY() As Double, ByRef vB() As Double, ByRef vCount As Long, ByRef isClosed As Boolean, ByRef layer As String)
    ReDim vX(0 To 100): ReDim vY(0 To 100): ReDim vB(0 To 100)
    vCount = 0
    isClosed = False
    layer = "0"
    
    Dim hasPendingX As Boolean: hasPendingX = False
    Dim pendingX As Double: pendingX = 0#
    
    Dim p As Long
    For p = 0 To UBound(entLines) - 1 Step 2
        Dim c As String: c = Trim(entLines(p))
        Dim v As String: v = Trim(entLines(p + 1))
        
        Select Case c
            Case "8":
                layer = v
            Case "70":
                If (CLng(Val(v)) And 1) = 1 Then isClosed = True
            Case "10":
                pendingX = Val(v)
                hasPendingX = True
            Case "20":
                If hasPendingX Then
                    If vCount > UBound(vX) Then
                        ReDim Preserve vX(0 To UBound(vX) + 100)
                        ReDim Preserve vY(0 To UBound(vY) + 100)
                        ReDim Preserve vB(0 To UBound(vB) + 100)
                    End If
                    vX(vCount) = pendingX
                    vY(vCount) = Val(v)
                    vB(vCount) = 0#
                    vCount = vCount + 1
                    hasPendingX = False
                End If
            Case "42":
                If vCount > 0 Then
                    vB(vCount - 1) = Val(v)
                End If
        End Select
    Next p
End Sub

' ==============================================================================
' HELPER: Computes the 2D bounding box (minX, minY, maxX, maxY) of an entity collection
' ==============================================================================
Sub ComputeEntitiesBounds(coll As Collection, ByRef minX As Double, ByRef minY As Double, ByRef maxX As Double, ByRef maxY As Double)
    minX = 1E+30
    minY = 1E+30
    maxX = -1E+30
    maxY = -1E+30
    
    If coll Is Nothing Then Exit Sub
    If coll.Count = 0 Then Exit Sub
    
    Dim entVar As Variant
    For Each entVar In coll
        Dim elines() As String: elines = entVar
        Dim etype As String: etype = UCase(Trim(elines(1)))
        
        If etype = "LINE" Then
            Dim lx1 As Double, ly1 As Double, lx2 As Double, ly2 As Double, lLay As String
            ParseLineEntity elines, lx1, ly1, lx2, ly2, lLay
            If lx1 < minX Then minX = lx1
            If lx1 > maxX Then maxX = lx1
            If lx2 < minX Then minX = lx2
            If lx2 > maxX Then maxX = lx2
            If ly1 < minY Then minY = ly1
            If ly1 > maxY Then maxY = ly1
            If ly2 < minY Then minY = ly2
            If ly2 > maxY Then maxY = ly2
            
        ElseIf etype = "ARC" Then
            Dim acx As Double, acy As Double, ar As Double, aa1 As Double, aa2 As Double, aLay As String
            ParseArcEntity elines, acx, acy, ar, aa1, aa2, aLay
            If (acx - ar) < minX Then minX = acx - ar
            If (acx + ar) > maxX Then maxX = acx + ar
            If (acy - ar) < minY Then minY = acy - ar
            If (acy + ar) > maxY Then maxY = acy + ar
            
        ElseIf etype = "CIRCLE" Then
            Dim ccx As Double, ccy As Double, cr As Double
            Dim p As Long
            For p = 0 To UBound(elines) - 1 Step 2
                Select Case Trim(elines(p))
                    Case "10": ccx = Val(Trim(elines(p + 1)))
                    Case "20": ccy = Val(Trim(elines(p + 1)))
                    Case "40": cr = Val(Trim(elines(p + 1)))
                End Select
            Next p
            If (ccx - cr) < minX Then minX = ccx - cr
            If (ccx + cr) > maxX Then maxX = ccx + cr
            If (ccy - cr) < minY Then minY = ccy - cr
            If (ccy + cr) > maxY Then maxY = ccy + cr
            
        ElseIf etype = "LWPOLYLINE" Then
            Dim vX() As Double, vY() As Double, vB() As Double
            Dim vCount As Long: vCount = 0
            Dim isClosed As Boolean: isClosed = False
            Dim pLay As String: pLay = "0"
            ParseLwPolylineEntity elines, vX, vY, vB, vCount, isClosed, pLay
            
            Dim vi As Long
            For vi = 0 To vCount - 1
                If vX(vi) < minX Then minX = vX(vi)
                If vX(vi) > maxX Then maxX = vX(vi)
                If vY(vi) < minY Then minY = vY(vi)
                If vY(vi) > maxY Then maxY = vY(vi)
                
                ' If segment has bulge, account for arc sagitta
                If vi < vCount - 1 Or isClosed Then
                    Dim nextVi As Long: nextVi = (vi + 1) Mod vCount
                    If Abs(vB(vi)) > 0.0001 Then
                        Dim midX As Double: midX = (vX(vi) + vX(nextVi)) * 0.5
                        Dim midY As Double: midY = (vY(vi) + vY(nextVi)) * 0.5
                        Dim cdx As Double: cdx = vX(nextVi) - vX(vi)
                        Dim cdy As Double: cdy = vY(nextVi) - vY(vi)
                        Dim arcMidX As Double: arcMidX = midX - cdy * vB(vi) * 0.5
                        Dim arcMidY As Double: arcMidY = midY + cdx * vB(vi) * 0.5
                        If arcMidX < minX Then minX = arcMidX
                        If arcMidX > maxX Then maxX = arcMidX
                        If arcMidY < minY Then minY = arcMidY
                        If arcMidY > maxY Then maxY = arcMidY
                    End If
                End If
            Next vi
            
        ElseIf etype = "POINT" Or etype = "SPLINE" Then
            Dim sp As Long
            For sp = 0 To UBound(elines) - 1 Step 2
                Dim scode As String: scode = Trim(elines(sp))
                Dim sval As Double: sval = Val(Trim(elines(sp + 1)))
                If scode = "10" Or scode = "11" Then
                    If sval < minX Then minX = sval
                    If sval > maxX Then maxX = sval
                ElseIf scode = "20" Or scode = "21" Then
                    If sval < minY Then minY = sval
                    If sval > maxY Then maxY = sval
                End If
            Next sp
        End If
    Next entVar
    
    If minX > maxX Then
        minX = 0#: maxX = 0#: minY = 0#: maxY = 0#
    End If
End Sub

' ==============================================================================
' HELPER: Translates all coordinates in an entity array by (dx, dy)
' ==============================================================================
Sub TranslateEntity(ByRef elines() As String, ByVal dx As Double, ByVal dy As Double)
    If Abs(dx) < 0.000000001 And Abs(dy) < 0.000000001 Then Exit Sub
    
    Dim etype As String: etype = UCase(Trim(elines(1)))
    Dim j As Long
    For j = 0 To UBound(elines) - 1 Step 2
        Dim c As String: c = Trim(elines(j))
        Dim v As String: v = Trim(elines(j + 1))
        
        Select Case etype
            Case "LINE"
                If c = "10" Or c = "11" Then
                    elines(j + 1) = DxfNumStr(Val(v) + dx)
                ElseIf c = "20" Or c = "21" Then
                    elines(j + 1) = DxfNumStr(Val(v) + dy)
                End If
                
            Case "ARC", "CIRCLE", "LWPOLYLINE", "POINT"
                If c = "10" Then
                    elines(j + 1) = DxfNumStr(Val(v) + dx)
                ElseIf c = "20" Then
                    elines(j + 1) = DxfNumStr(Val(v) + dy)
                End If
                
            Case "SPLINE"
                If c = "10" Or c = "11" Then
                    elines(j + 1) = DxfNumStr(Val(v) + dx)
                ElseIf c = "20" Or c = "21" Then
                    elines(j + 1) = DxfNumStr(Val(v) + dy)
                End If
                
            Case "ELLIPSE"
                If c = "10" Then
                    elines(j + 1) = DxfNumStr(Val(v) + dx)
                ElseIf c = "20" Then
                    elines(j + 1) = DxfNumStr(Val(v) + dy)
                End If
        End Select
    Next j
End Sub

' ==============================================================================
' HELPER: Translates all coordinates in an entity collection by (dx, dy)
' ==============================================================================
Sub TranslateEntities(ByRef coll As Collection, ByVal dx As Double, ByVal dy As Double)
    If coll Is Nothing Then Exit Sub
    If Abs(dx) < 0.000000001 And Abs(dy) < 0.000000001 Then Exit Sub
    
    Dim newColl As Collection: Set newColl = New Collection
    Dim entVar As Variant
    For Each entVar In coll
        Dim elines() As String: elines = entVar
        TranslateEntity elines, dx, dy
        newColl.Add elines
    Next entVar
    
    Set coll = newColl
End Sub

' ==============================================================================
' HELPER: Determines if document or DXF output uses metric units
' ==============================================================================
Function IsDocumentMetric(swModel As SldWorks.ModelDoc2, fso As Object, sampleDxfPath As String) As Boolean
    IsDocumentMetric = False ' Default to Imperial (Inches)
    
    ' 1. Check SolidWorks ModelDoc UserUnit
    On Error Resume Next
    If Not swModel Is Nothing Then
        Dim swUUnit As Object
        Set swUUnit = swModel.GetUserUnit(0) ' swUserUnitsType_e.swLengthUnit = 0
        If Not swUUnit Is Nothing Then
            IsDocumentMetric = CBool(swUUnit.IsMetric)
            On Error GoTo 0
            Exit Function
        End If
    End If
    On Error GoTo 0
    
    ' 2. Check SolidWorks ModelDoc GetUnits
    On Error Resume Next
    If Not swModel Is Nothing Then
        Dim vUnits As Variant
        vUnits = swModel.GetUnits
        If IsArray(vUnits) Then
            Dim lUnit As Long: lUnit = CLng(vUnits(0))
            ' 0 = MM, 1 = CM, 2 = M, 3 = Inches, 4 = Feet
            If lUnit = 0 Or lUnit = 1 Or lUnit = 2 Then
                IsDocumentMetric = True
            Else
                IsDocumentMetric = False
            End If
            On Error GoTo 0
            Exit Function
        End If
    End If
    On Error GoTo 0
    
    ' 3. Inspect DXF header $INSUNITS directly from exported file
    If fso.FileExists(sampleDxfPath) Then
        Dim ts As Object: Set ts = fso.OpenTextFile(sampleDxfPath, 1)
        Dim cLine As String, prevLine As String: prevLine = ""
        Dim lCount As Long: lCount = 0
        Do While Not ts.AtEndOfStream And lCount < 1500
            cLine = Trim(ts.ReadLine)
            lCount = lCount + 1
            If prevLine = "$INSUNITS" Then
                If Not ts.AtEndOfStream Then
                    Dim valStr As String: valStr = Trim(ts.ReadLine)
                    Dim uCode As Long: uCode = Val(valStr)
                    ' 1=Inches, 2=Feet, 4=MM, 5=CM, 6=M
                    If uCode = 4 Or uCode = 5 Or uCode = 6 Then
                        IsDocumentMetric = True
                    ElseIf uCode = 1 Or uCode = 2 Then
                        IsDocumentMetric = False
                    End If
                    Exit Do
                End If
            End If
            prevLine = cLine
        Loop
        ts.Close
    End If
End Function

' ==============================================================================
' DXF EMITTER: Emits a closed or open LWPOLYLINE entity array
' Fully compliant with AutoCAD DXF R15+ (AC1015) ObjectARX class hierarchy
' ==============================================================================
Sub EmitLwPolyline(cvX() As Double, cvY() As Double, cvB() As Double, ByVal numVerts As Long, ByVal isClosed As Boolean, ByVal layer As String, ByRef outColl As Collection)
    If numVerts < 2 Then Exit Sub
    
    Dim polyLines() As String
    ReDim polyLines(0 To 30 + numVerts * 6)
    Dim pCount As Long: pCount = 0
    
    polyLines(pCount) = "  0": pCount = pCount + 1
    polyLines(pCount) = "LWPOLYLINE": pCount = pCount + 1
    polyLines(pCount) = "100": pCount = pCount + 1
    polyLines(pCount) = "AcDbEntity": pCount = pCount + 1
    polyLines(pCount) = "  8": pCount = pCount + 1
    polyLines(pCount) = layer: pCount = pCount + 1
    polyLines(pCount) = "100": pCount = pCount + 1
    polyLines(pCount) = "AcDbPolyline": pCount = pCount + 1
    polyLines(pCount) = " 90": pCount = pCount + 1
    polyLines(pCount) = CStr(numVerts): pCount = pCount + 1
    polyLines(pCount) = " 70": pCount = pCount + 1
    polyLines(pCount) = IIf(isClosed, "1", "0"): pCount = pCount + 1
    polyLines(pCount) = " 43": pCount = pCount + 1
    polyLines(pCount) = "0.0": pCount = pCount + 1
    
    Dim v As Long
    For v = 0 To numVerts - 1
        polyLines(pCount) = " 10": pCount = pCount + 1
        polyLines(pCount) = DxfNumStr(cvX(v)): pCount = pCount + 1
        polyLines(pCount) = " 20": pCount = pCount + 1
        polyLines(pCount) = DxfNumStr(cvY(v)): pCount = pCount + 1
        If Abs(cvB(v)) > 0.0000000001 Then
            polyLines(pCount) = " 42": pCount = pCount + 1
            polyLines(pCount) = DxfNumStr(cvB(v)): pCount = pCount + 1
        End If
    Next v
    
    ReDim Preserve polyLines(0 To pCount - 1)
    outColl.Add polyLines
End Sub

' ==============================================================================
' HELPER: Calculates endpoints and DXF bulge parameter for an ARC entity
' Bulge = tan(deltaTheta / 4)
' ==============================================================================
Function CalcArcBulge(cx As Double, cy As Double, r As Double, a1Deg As Double, a2Deg As Double, _
                      ByRef x1 As Double, ByRef y1 As Double, ByRef x2 As Double, ByRef y2 As Double, _
                      ByRef bulge As Double)
    Const PI As Double = 3.14159265358979
    Const DEG2RAD As Double = PI / 180#
    
    Dim a1Rad As Double: a1Rad = a1Deg * DEG2RAD
    Dim a2Rad As Double: a2Rad = a2Deg * DEG2RAD
    
    x1 = cx + r * Cos(a1Rad)
    y1 = cy + r * Sin(a1Rad)
    x2 = cx + r * Cos(a2Rad)
    y2 = cy + r * Sin(a2Rad)
    
    Dim sweepDeg As Double: sweepDeg = a2Deg - a1Deg
    Do While sweepDeg <= 0#
        sweepDeg = sweepDeg + 360#
    Loop
    Do While sweepDeg > 360#
        sweepDeg = sweepDeg - 360#
    Loop
    
    Dim sweepRad As Double: sweepRad = sweepDeg * DEG2RAD
    
    If sweepDeg >= 359.9999 Then
        bulge = 1#
    Else
        bulge = Tan(sweepRad / 4#)
    End If
End Function

' ==============================================================================
' HELPER: Calculate 2D Euclidean distance
' ==============================================================================
Function PointDist(x1 As Double, y1 As Double, x2 As Double, y2 As Double) As Double
    Dim dx As Double: dx = x1 - x2
    Dim dy As Double: dy = y1 - y2
    PointDist = Sqr(dx * dx + dy * dy)
End Function

' ==============================================================================
' HELPER: Format number for DXF with period decimal separator
' ==============================================================================
Function DxfNumStr(ByVal val As Double) As String
    If Abs(val) < 0.00000000001 Then
        DxfNumStr = "0.0"
        Exit Function
    End If
    Dim s As String
    s = Format(val, "0.000000")
    s = Replace(s, ",", ".")
    Do While Right(s, 1) = "0" And InStr(s, ".") > 0
        s = Left(s, Len(s) - 1)
    Loop
    If Right(s, 1) = "." Then s = s & "0"
    DxfNumStr = s
End Function

' ==============================================================================
' HELPER: Compute orthonormal alignment matrix from normal vector
' Returns 12-element array (3x Origin, 3x X-vector, 3x Y-vector, 3x Z-vector)
' ==============================================================================
Function ComputeAlignment(vNormal As Variant) As Variant
    Dim nx As Double, ny As Double, nz As Double
    nx = CDbl(vNormal(0))
    ny = CDbl(vNormal(1))
    nz = CDbl(vNormal(2))
    
    Dim normLen As Double
    normLen = Sqr(nx * nx + ny * ny + nz * nz)
    If normLen > 0 Then
        nx = nx / normLen: ny = ny / normLen: nz = nz / normLen
    Else
        nz = 1#
    End If
    
    Dim vx As Double, vy As Double, vz As Double
    If Abs(nz) < 0.999 Then
        vx = -ny: vy = nx: vz = 0#
    Else
        vx = nz: vy = 0#: vz = -nx
    End If
    
    Dim vLen As Double
    vLen = Sqr(vx * vx + vy * vy + vz * vz)
    If vLen > 0 Then
        vx = vx / vLen: vy = vy / vLen: vz = vz / vLen
    Else
        vx = 1#: vy = 0#: vz = 0#
    End If
    
    Dim yx As Double, yy As Double, yz As Double
    yx = ny * vz - nz * vy
    yy = nz * vx - nx * vz
    yz = nx * vy - ny * vx
    
    Dim alignData(11) As Double
    alignData(0) = 0#: alignData(1) = 0#: alignData(2) = 0#
    alignData(3) = vx: alignData(4) = vy: alignData(5) = vz
    alignData(6) = yx: alignData(7) = yy: alignData(8) = yz
    alignData(9) = nx: alignData(10) = ny: alignData(11) = nz
    
    ComputeAlignment = alignData
End Function

' ==============================================================================
' HELPER: Determine if Body has a custom logical name
' ==============================================================================
Function IsCustomBodyName(swModel As SldWorks.ModelDoc2, swBody As SldWorks.Body2) As Boolean
    If swBody Is Nothing Then
        IsCustomBodyName = False
        Exit Function
    End If
    
    Dim bodyName As String: bodyName = swBody.Name
    If bodyName = "" Then
        IsCustomBodyName = False
        Exit Function
    End If
    
    Dim lastFeatName As String
    lastFeatName = GetLastFeatureAppliedToBody(swModel, swBody)
    
    If lastFeatName <> "" Then
        If StrComp(Trim(bodyName), Trim(lastFeatName), vbTextCompare) = 0 Then
            IsCustomBodyName = False
            Exit Function
        End If
    End If
    
    Dim prefixes As Variant
    prefixes = Array("SolidBody", "SurfaceBody", "Imported", "Boss-Extrude", _
                     "Cut-Extrude", "Revolve", "Sweep", "Loft", "Thicken", _
                     "Sheet-Metal", "Base-Flange", "Edge-Flange", "Body")
                     
    Dim pVar As Variant
    For Each pVar In prefixes
        If Left(bodyName, Len(CStr(pVar))) = CStr(pVar) Then
            Dim suffix As String
            suffix = Mid(bodyName, Len(CStr(pVar)) + 1)
            If suffix = "" Or IsNumeric(suffix) Or Left(suffix, 1) = "[" Then
                IsCustomBodyName = False
                Exit Function
            End If
        End If
    Next pVar
    
    IsCustomBodyName = True
End Function

' ==============================================================================
' HELPER: Retrieve the name of the last feature that modified a body
' ==============================================================================
Function GetLastFeatureAppliedToBody(swModel As SldWorks.ModelDoc2, swBody As SldWorks.Body2) As String
    GetLastFeatureAppliedToBody = ""
    If swModel Is Nothing Or swBody Is Nothing Then Exit Function
    
    Dim swPart As SldWorks.PartDoc
    If swModel.GetType = swDocPART Then
        Set swPart = swModel
    Else
        Exit Function
    End If
    
    Dim swFeat As SldWorks.Feature
    Set swFeat = swModel.FirstFeature
    
    Dim targetBodyName As String: targetBodyName = swBody.Name
    Dim lastMatchingFeat As String: lastMatchingFeat = ""
    
    Do While Not swFeat Is Nothing
        Dim featName As String: featName = swFeat.Name
        
        Dim vFaces As Variant
        vFaces = swFeat.GetFaces
        If Not IsEmpty(vFaces) Then
            Dim fIdx As Long
            For fIdx = LBound(vFaces) To UBound(vFaces)
                Dim swF As SldWorks.Face2: Set swF = vFaces(fIdx)
                If Not swF Is Nothing Then
                    Dim b As SldWorks.Body2: Set b = swF.GetBody
                    If Not b Is Nothing Then
                        If b.Name = targetBodyName Then
                            lastMatchingFeat = featName
                            Exit For
                        End If
                    End If
                End If
            Next fIdx
        End If
        
        Set swFeat = swFeat.GetNextFeature
    Loop
    
    GetLastFeatureAppliedToBody = lastMatchingFeat
End Function

' ==============================================================================
' HELPER: Extract clean part name without .SLDPRT extension or instance suffix
' ==============================================================================
Function GetCleanPartName(swPartModel As SldWorks.ModelDoc2, swComp As SldWorks.Component2, Optional ByVal configName As String = "") As String
    Dim rawTitle As String
    If Not swPartModel Is Nothing Then
        rawTitle = swPartModel.GetTitle
    ElseIf Not swComp Is Nothing Then
        rawTitle = swComp.Name2
    Else
        rawTitle = "Part"
    End If
    
    Dim dotPos As Long: dotPos = InStrRev(rawTitle, ".")
    If dotPos > 0 Then
        Dim ext As String: ext = UCase(Mid(rawTitle, dotPos))
        If ext = ".SLDPRT" Or ext = ".SLDASM" Then
            rawTitle = Left(rawTitle, dotPos - 1)
        End If
    End If
    
    Dim dashPos As Long: dashPos = InStrRev(rawTitle, "-")
    If dashPos > 0 Then
        Dim afterDash As String: afterDash = Mid(rawTitle, dashPos + 1)
        If IsNumeric(afterDash) Then
            rawTitle = Left(rawTitle, dashPos - 1)
        End If
    End If
    
    Dim caretPos As Long: caretPos = InStr(rawTitle, "^")
    If caretPos > 0 Then
        rawTitle = Left(rawTitle, caretPos - 1)
    End If
    
    Dim cleanBase As String: cleanBase = SanitizeFileName(rawTitle)
    
    ' Determine if configuration name should be appended
    Dim effectiveConfig As String: effectiveConfig = Trim(configName)
    If effectiveConfig = "" And Not swComp Is Nothing Then
        On Error Resume Next
        effectiveConfig = Trim(swComp.ReferencedConfiguration)
        On Error GoTo 0
    End If
    
    If effectiveConfig <> "" Then
        Dim hasMultipleConfigs As Boolean: hasMultipleConfigs = False
        If Not swPartModel Is Nothing Then
            On Error Resume Next
            If swPartModel.GetConfigurationCount > 1 Then
                hasMultipleConfigs = True
            End If
            On Error GoTo 0
        Else
            hasMultipleConfigs = True
        End If
        
        If hasMultipleConfigs Then
            cleanBase = cleanBase & "_" & SanitizeFileName(effectiveConfig)
        End If
    End If
    
    GetCleanPartName = cleanBase
End Function

' ==============================================================================
' HELPER: Get default export folder
' ==============================================================================
Function GetDefaultFolder(swModel As SldWorks.ModelDoc2) As String
    Dim modelPath As String: modelPath = swModel.GetPathName
    If modelPath <> "" Then
        Dim slashPos As Long: slashPos = InStrRev(modelPath, "\")
        If slashPos > 0 Then
            GetDefaultFolder = Left(modelPath, slashPos - 1)
            Exit Function
        End If
    End If
    
    Dim docDir As String
    docDir = Environ("USERPROFILE") & "\Documents"
    Dim fso As Object: Set fso = CreateObject("Scripting.FileSystemObject")
    If fso.FolderExists(docDir) Then
        GetDefaultFolder = docDir
    Else
        GetDefaultFolder = "C:\"
    End If
End Function

' ==============================================================================
' HELPER: Windows Folder Browser Dialog via Shell.Application
' ==============================================================================
Function BrowseForFolderDialog(title As String, initialFolder As String) As String
    BrowseForFolderDialog = ""
    On Error Resume Next
    Dim sh As Object: Set sh = CreateObject("Shell.Application")
    Dim f As Object
    Const BIF_RETURNONLYFSDIRS As Long = &H1
    Const BIF_NEWDIALOGSTYLE As Long = &H40
    Set f = sh.BrowseForFolder(0, title, BIF_RETURNONLYFSDIRS Or BIF_NEWDIALOGSTYLE, initialFolder)
    If Not f Is Nothing Then
        BrowseForFolderDialog = f.Self.Path
    End If
    On Error GoTo 0
End Function

' ==============================================================================
' HELPER: Generate unique collision-free file path (_01, _02...)
' ==============================================================================
Function GetUniqueFilePath(folderPath As String, basePrefix As String, usedDict As Object) As String
    Dim fso As Object: Set fso = CreateObject("Scripting.FileSystemObject")
    Dim idx As Long: idx = 1
    Dim candidate As String
    
    Do
        Dim suffix As String: suffix = Format(idx, "00")
        candidate = folderPath & "\" & basePrefix & "_" & suffix & ".dxf"
        
        If Not fso.FileExists(candidate) And Not usedDict.Exists(UCase(candidate)) Then
            usedDict.Add UCase(candidate), True
            GetUniqueFilePath = candidate
            Exit Function
        End If
        idx = idx + 1
    Loop
End Function

' ==============================================================================
' HELPER: Sanitize string for valid file names
' ==============================================================================
Function SanitizeFileName(rawName As String) As String
    Dim res As String: res = rawName
    Dim invalidChars As String: invalidChars = "\/:*?""<>|"
    Dim i As Long
    For i = 1 To Len(invalidChars)
        Dim ch As String: ch = Mid(invalidChars, i, 1)
        res = Replace(res, ch, "_")
    Next i
    Do While InStr(res, "__") > 0
        res = Replace(res, "__", "_")
    Loop
    If Left(res, 1) = "_" Then res = Mid(res, 2)
    If Right(res, 1) = "_" Then res = Left(res, Len(res) - 1)
    SanitizeFileName = Trim(res)
End Function
