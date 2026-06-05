using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace SunlightPlugin
{
    public class ProjectState
    {
        public List<ObjectId> BoundaryIds = new List<ObjectId>();
        public List<ObjectId> StaticBldgIds = new List<ObjectId>();
        public List<GridNode> BaseGridCache = new List<GridNode>();

        // 【新增】：专门追踪当前激活边界所生成的日照网格，方便局部更新
        public List<ObjectId> CurrentResultMeshes = new List<ObjectId>();
        public Dictionary<short, ObjectId> CurrentResultMeshByColor = new Dictionary<short, ObjectId>();

        public SunlightCalculatorGPU GpuEngine = null;
        public int TotalRaysGlobal = 0;

        public ProjectState()
        {
            try { GpuEngine = new SunlightCalculatorGPU(); } catch { }
        }
    }

    public partial class SunControlUI : UserControl
    {
        private const string UiVersion = "1.2.0";
        private const string BuildingMetaDictName = "SUN_BUILDING_META";
        private const string FacadeMetaRegAppName = "SUN_FACADE_META";
        private const string DefaultTimeStepMinutesText = "5";

        private struct BuildingParamData
        {
            public double Height;
            public double Elevation;
            public int Floors;
            public double FloorHeight;
            public double ElevationDiff;
            public double Parapet;
            public bool UseInputHeight;
        }

        private struct UiCalcParams
        {
            public int TimeStep;
            public double Latitude;
            public double Spacing;
            public double CalcZ;
            public bool IsWinter;
        }

        private struct FacadeSampleCell
        {
            public Point3d Center;
            public Point3d[] Corners;
            public string Side;
            public ObjectId TargetId;
        }

        private struct BuildingBounds2D
        {
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;

            public bool Contains(Point2d point)
            {
                return point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;
            }

            public bool Intersects(BuildingBounds2D other)
            {
                return !(other.MinX > MaxX || other.MaxX < MinX || other.MinY > MaxY || other.MaxY < MinY);
            }
        }

        private Dictionary<Document, ProjectState> _states = new Dictionary<Document, ProjectState>();
        private Dictionary<string, List<CityData>> _locationDB;
        private List<CityData> _currentFilteredCities = new List<CityData>();
        private List<string> _currentFilteredProvinces = new List<string>();
        private bool _isSettingLoaded = false; // 【新增】：防止读取图纸设置时被事件覆盖的锁
        private string _sunRayCacheKey;
        private List<System.Numerics.Vector3> _sunRayCache;
        private const bool PerfLogEnabled = true;
        private const int RealtimeThrottleMs = 100;
        private const int RealtimeThrottleMinMs = 60;
        private const int RealtimeThrottleMaxMs = 220;
        private const int RealtimeTargetFrameMs = 100;

        private Document _realtimeHookedDoc;
        private bool _realtimeModeEnabled = true;
        private bool _isSyncingBuildingParams = false;
        private bool _isApplyingBuildingParams = false;
        private ObjectId _selectedBuildingId = ObjectId.Null;
        private bool _realtimeTrackingCommand = false;
        private bool _realtimeRecalcRunning = false;
        private bool _realtimeRecalcPending = false;
        private bool _realtimeEndCleanupPending = false;
        private bool _realtimeNeedsFinalRecalc = false;
        private bool _isAutoMatchingProvinceByCity = false;
        private bool _isAutoPersistingDwgSettings = false;
        private bool _facadeRealtimeEnabled = false;
        private readonly HashSet<ObjectId> _facadeTargetIds = new HashSet<ObjectId>();
        private DateTime _realtimeLastRecalcUtc = DateTime.MinValue;
        private int _realtimeAdaptiveThrottleMs = RealtimeThrottleMs;
        private readonly HashSet<ObjectId> _realtimeDirtyIds = new HashSet<ObjectId>();
        private readonly Dictionary<ObjectId, BuildingBounds2D> _realtimePreModifyBounds = new Dictionary<ObjectId, BuildingBounds2D>();

        private static void PerfLog(Editor editor, string phase, Stopwatch sw)
        {
            if (!PerfLogEnabled || editor == null || sw == null) return;
            editor.WriteMessage($"\n[PERF] {phase}: {sw.ElapsedMilliseconds} ms");
        }

        private static bool TryParseDouble(string text, out double value)
        {
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out value))
            {
                return true;
            }
            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static bool IsRealisticStyleName(string styleName)
        {
            if (string.IsNullOrWhiteSpace(styleName)) return false;
            return string.Equals(styleName, "Realistic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(styleName, "真实", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryInvokeVisualStyleCommand(Editor editor, string styleName)
        {
            if (editor == null || string.IsNullOrWhiteSpace(styleName)) return false;

            // 优先尝试系统变量直设，避免部分版本缺失 Editor.Command API。
            try
            {
                Application.SetSystemVariable("VSCURRENT", styleName);
                return true;
            }
            catch
            {
            }

            try
            {
                var commandMethod = typeof(Editor).GetMethod("Command", new Type[] { typeof(object[]) });
                if (commandMethod != null)
                {
                    object[] args = new object[] { "._SHADEMODE", "_Realistic" };
                    commandMethod.Invoke(editor, new object[] { args });
                    return true;
                }
            }
            catch
            {
            }

            // 同步 COM 兜底：在进入 Jig 前直接执行完整命令，避免异步注入到取点流。
            try
            {
                dynamic acadApp = Application.AcadApplication;
                if (acadApp == null) return false;
                dynamic acadDoc = acadApp.ActiveDocument;
                if (acadDoc == null) return false;

                // 真实样式命令（完整参数，避免交互停顿）
                acadDoc.SendCommand("._SHADEMODE _R ");
                return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool TrySwitchToRealistic(Editor editor, out string diagnostics)
        {
            var trace = new List<string>();
            string[] candidates = new string[] { "Realistic", "真实" };
            foreach (string candidate in candidates)
            {
                bool invoked = TryInvokeVisualStyleCommand(editor, candidate);
                trace.Add("cmd(" + candidate + ") invoked=" + invoked);
                if (invoked)
                {
                    diagnostics = string.Join(" | ", trace);
                    return true;
                }
            }

            diagnostics = string.Join(" | ", trace);
            return false;
        }

        private bool TryReadUiCalcParams(bool needSpacing, bool needCalcHeight, out UiCalcParams parameters, out string error)
        {
            parameters = default(UiCalcParams);
            error = null;

            if (!int.TryParse(txtTimeStep.Text, out int timeStep) || timeStep <= 0)
            {
                error = "计算步长必须是大于 0 的整数（分钟）。";
                return false;
            }

            if (!TryParseDouble(txtLatitude.Text, out double lat) || lat < -90.0 || lat > 90.0)
            {
                error = "纬度必须是 -90 到 90 之间的数字。";
                return false;
            }

            double spacing = 0.0;
            if (needSpacing)
            {
                if (!TryParseDouble(txtGridSpacing.Text, out spacing) || spacing <= 0)
                {
                    error = "网格间距必须是大于 0 的数字。";
                    return false;
                }
            }

            double calcZ = 0.0;
            if (needCalcHeight && txtCalcHeight != null && !string.IsNullOrWhiteSpace(txtCalcHeight.Text) && !TryParseDouble(txtCalcHeight.Text, out calcZ))
            {
                error = "计算高度必须是数字。";
                return false;
            }

            parameters = new UiCalcParams
            {
                TimeStep = timeStep,
                Latitude = lat,
                Spacing = spacing,
                CalcZ = calcZ,
                IsWinter = (cmbDate.SelectedIndex == 1)
            };
            return true;
        }

        private List<System.Numerics.Vector3> GetOrCreateSunRays(double latitude, bool isWinterSolstice, int timeStep)
        {
            string key = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F6}|{1}|{2}", latitude, isWinterSolstice ? 1 : 0, timeStep);
            if (_sunRayCache != null && string.Equals(_sunRayCacheKey, key, StringComparison.Ordinal))
            {
                return _sunRayCache;
            }

            _sunRayCache = SunEngine.GenerateSunRays(latitude, isWinterSolstice, timeStep);
            _sunRayCacheKey = key;
            return _sunRayCache;
        }

        private static List<BuildingBounds2D> BuildBuildingBounds(List<SimpleBuilding> buildings)
        {
            var bounds = new List<BuildingBounds2D>(buildings.Count);
            for (int i = 0; i < buildings.Count; i++)
            {
                var vertices = buildings[i].Vertices;
                if (vertices == null || vertices.Count == 0)
                {
                    bounds.Add(new BuildingBounds2D());
                    continue;
                }

                double minX = double.MaxValue;
                double minY = double.MaxValue;
                double maxX = double.MinValue;
                double maxY = double.MinValue;

                for (int v = 0; v < vertices.Count; v++)
                {
                    var p = vertices[v];
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }

                bounds.Add(new BuildingBounds2D { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY });
            }
            return bounds;
        }

        private static BuildingBounds2D ExpandBounds(BuildingBounds2D bounds, double margin)
        {
            return new BuildingBounds2D
            {
                MinX = bounds.MinX - margin,
                MinY = bounds.MinY - margin,
                MaxX = bounds.MaxX + margin,
                MaxY = bounds.MaxY + margin
            };
        }

        private static bool IsInsideAnyBounds(Point2d point, List<BuildingBounds2D> bounds)
        {
            for (int i = 0; i < bounds.Count; i++)
            {
                if (bounds[i].Contains(point)) return true;
            }
            return false;
        }

        private static bool TryGetEntityBounds2D(Entity ent, out BuildingBounds2D bounds)
        {
            bounds = default(BuildingBounds2D);
            if (ent == null || ent.IsErased) return false;
            try
            {
                var ext = ent.GeometricExtents;
                bounds = new BuildingBounds2D
                {
                    MinX = ext.MinPoint.X,
                    MinY = ext.MinPoint.Y,
                    MaxX = ext.MaxPoint.X,
                    MaxY = ext.MaxPoint.Y
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildCurrentBuildingCache(Transaction tr, ProjectState state, out List<SimpleBuilding> currentBldgCache, out List<BuildingBounds2D> currentBldgBounds)
        {
            currentBldgCache = new List<SimpleBuilding>(state.StaticBldgIds.Count);
            foreach (ObjectId id in state.StaticBldgIds)
            {
                if (id.IsErased) continue;
                if (TryExtractBuilding(tr.GetObject(id, OpenMode.ForRead) as Entity, tr, out SimpleBuilding bldg))
                {
                    currentBldgCache.Add(bldg);
                }
            }

            currentBldgBounds = BuildBuildingBounds(currentBldgCache);
            return true;
        }

        public SunControlUI()
        {
            InitializeComponent();
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this)) return;

            if (txtAppVersion != null)
            {
                txtAppVersion.Text = "版本 " + UiVersion;
            }

            try
            {
                _locationDB = CityDatabase.GetLocationDB();
                RefreshProvinceListByFilter(string.Empty);
                if (cmbProvince.Items.Count > 0)
                {
                    cmbProvince.SelectedIndex = 0;
                }
            }
            catch { }

            EnsureDefaultUiValues();

            UpdateSharedPushButtonLabel();

            HookBuildingParamEditors();

            _realtimeModeEnabled = chkRealtimeMode == null || chkRealtimeMode.IsChecked == true;
            EnsureRealtimeHooks(Application.DocumentManager.MdiActiveDocument);

            Application.DocumentManager.DocumentActivated += (s, e) =>
            {
                if (e?.Document == null) return;
                EnsureRealtimeHooks(e.Document);
                TryLoadDwgSettings(silent: true);
            };

            Application.DocumentManager.DocumentToBeDestroyed += (s, e) =>
            {
                if (ReferenceEquals(_realtimeHookedDoc, e.Document))
                {
                    DetachRealtimeHooks();
                }

                if (_states.ContainsKey(e.Document))
                {
                    _states[e.Document].GpuEngine?.Dispose();
                    _states.Remove(e.Document);
                }
            };
        }

        private ProjectState GetState()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return null;
            EnsureRealtimeHooks(doc);
            if (!_states.ContainsKey(doc)) _states[doc] = new ProjectState();
            return _states[doc];
        }

        private void HookBuildingParamEditors()
        {
            if (txtTotalHeight != null) txtTotalHeight.LostKeyboardFocus += BuildingParamsEditor_LostKeyboardFocus;
            if (txtFloors != null) txtFloors.LostKeyboardFocus += BuildingParamsEditor_LostKeyboardFocus;
            if (txtFloorHeight != null) txtFloorHeight.LostKeyboardFocus += BuildingParamsEditor_LostKeyboardFocus;
            if (txtElevationDiff != null) txtElevationDiff.LostKeyboardFocus += BuildingParamsEditor_LostKeyboardFocus;
            if (txtParapet != null) txtParapet.LostKeyboardFocus += BuildingParamsEditor_LostKeyboardFocus;
            if (rbInputHeight != null) rbInputHeight.Checked += BuildingParamsEditor_Changed;
            if (rbCalcHeight != null) rbCalcHeight.Checked += BuildingParamsEditor_Changed;
        }

        private void EnsureDefaultUiValues()
        {
            if (txtTimeStep == null) return;
            if (!int.TryParse(txtTimeStep.Text, out int step) || step <= 0)
            {
                txtTimeStep.Text = DefaultTimeStepMinutesText;
            }
        }

        private bool IsFacadeTabSelected()
        {
            return tabCalcMode != null && tabCalcMode.SelectedIndex == 1;
        }

        private void UpdateSharedPushButtonLabel()
        {
            if (btnStep3 == null) return;
            string mode = IsFacadeTabSelected() ? "立面" : "场地";
            btnStep3.Content = $"④ 启动共用推敲 (当前:{mode})";
        }

        private void tabCalcMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (btnStep3 == null || btnStep3.IsChecked == true) return;
            UpdateSharedPushButtonLabel();
        }

        private void BuildingParamsEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            TryApplyUiParamsToSelectedBuilding();
        }

        private void BuildingParamsEditor_Changed(object sender, RoutedEventArgs e)
        {
            TryApplyUiParamsToSelectedBuilding();
        }

        private List<ObjectId> CollectClosedBoundaries(Document doc, Editor ed)
        {
            var result = new List<ObjectId>();
            var unique = new HashSet<ObjectId>();

            PromptSelectionResult psr = ed.SelectImplied();
            if (psr.Status != PromptStatus.OK || psr.Value.Count == 0)
            {
                Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
                var pso = new PromptSelectionOptions
                {
                    MessageForAdding = "\n请选择一个或多个闭合多段线作为计算边界: "
                };
                var filter = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                });
                psr = ed.GetSelection(pso, filter);
            }

            if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
            {
                return result;
            }

            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in psr.Value.GetObjectIds())
                {
                    var poly = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (poly == null || !poly.Closed) continue;
                    if (!unique.Add(id)) continue;
                    result.Add(id);
                }
                tr.Commit();
            }

            return result;
        }

        private bool TryGetActiveBoundaries(Transaction tr, ProjectState state, out List<Polyline> boundaries, out string error)
        {
            boundaries = new List<Polyline>();
            error = null;

            if (state == null || state.BoundaryIds == null || state.BoundaryIds.Count == 0)
            {
                error = "计算边界无效，请重新设定边界！";
                return false;
            }

            foreach (ObjectId id in state.BoundaryIds)
            {
                if (id == ObjectId.Null || id.IsErased) continue;
                var poly = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (poly == null || !poly.Closed) continue;
                boundaries.Add(poly);
            }

            if (boundaries.Count == 0)
            {
                error = "计算边界无效，请重新设定边界！";
                return false;
            }

            return true;
        }

        private static bool IsWatchedRealtimeCommand(string globalCommandName)
        {
            if (string.IsNullOrWhiteSpace(globalCommandName)) return false;
            string name = globalCommandName.Trim().TrimStart('.', '_');
            return string.Equals(name, "MOVE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ROTATE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRealtimeCandidateEntity(Entity ent)
        {
            if (ent == null || ent.IsErased) return false;
            if (ent is Polyline || ent is BlockReference)
            {
                return string.Equals(ent.Layer, "SUN_BUILD", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private void EnsureRealtimeHooks(Document doc)
        {
            if (doc == null) return;
            if (ReferenceEquals(_realtimeHookedDoc, doc)) return;

            DetachRealtimeHooks();
            _realtimeHookedDoc = doc;
            _realtimeHookedDoc.CommandWillStart += Realtime_CommandWillStart;
            _realtimeHookedDoc.CommandEnded += Realtime_CommandEnded;
            _realtimeHookedDoc.CommandCancelled += Realtime_CommandEnded;
            _realtimeHookedDoc.CommandFailed += Realtime_CommandEnded;
            _realtimeHookedDoc.Editor.PointMonitor += Realtime_PointMonitor;
            _realtimeHookedDoc.ImpliedSelectionChanged += Realtime_ImpliedSelectionChanged;
            _realtimeHookedDoc.Database.ObjectOpenedForModify += Realtime_ObjectOpenedForModify;
            _realtimeHookedDoc.Database.ObjectModified += Realtime_ObjectModified;

            TryLoadDwgSettings(silent: true);
        }

        private void DetachRealtimeHooks()
        {
            if (_realtimeHookedDoc == null) return;

            try { _realtimeHookedDoc.CommandWillStart -= Realtime_CommandWillStart; } catch { }
            try { _realtimeHookedDoc.CommandEnded -= Realtime_CommandEnded; } catch { }
            try { _realtimeHookedDoc.CommandCancelled -= Realtime_CommandEnded; } catch { }
            try { _realtimeHookedDoc.CommandFailed -= Realtime_CommandEnded; } catch { }
            try { _realtimeHookedDoc.Editor.PointMonitor -= Realtime_PointMonitor; } catch { }
            try { _realtimeHookedDoc.ImpliedSelectionChanged -= Realtime_ImpliedSelectionChanged; } catch { }
            try { _realtimeHookedDoc.Database.ObjectOpenedForModify -= Realtime_ObjectOpenedForModify; } catch { }
            try { _realtimeHookedDoc.Database.ObjectModified -= Realtime_ObjectModified; } catch { }

            _realtimeHookedDoc = null;
            _realtimeTrackingCommand = false;
            _realtimeDirtyIds.Clear();
            _realtimeNeedsFinalRecalc = false;
            _realtimeRecalcRunning = false;
            _realtimeRecalcPending = false;
            _realtimeEndCleanupPending = false;
            _realtimeLastRecalcUtc = DateTime.MinValue;
            _realtimeAdaptiveThrottleMs = RealtimeThrottleMs;
            _realtimePreModifyBounds.Clear();
            _selectedBuildingId = ObjectId.Null;
        }

        private static bool IsPluginBuildingBlock(BlockReference br, Transaction tr)
        {
            if (br == null) return false;
            if (string.Equals(br.Layer, "SUN_BUILD", StringComparison.OrdinalIgnoreCase)) return true;
            var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return false;
            return btr.Name.StartsWith("Bldg_", StringComparison.OrdinalIgnoreCase);
        }

        private static ResultBuffer BuildBuildingMetaBuffer(BuildingParamData data)
        {
            return new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "v1"),
                new TypedValue((int)DxfCode.Real, data.Height),
                new TypedValue((int)DxfCode.Real, data.Elevation),
                new TypedValue((int)DxfCode.Int32, data.Floors),
                new TypedValue((int)DxfCode.Real, data.FloorHeight),
                new TypedValue((int)DxfCode.Real, data.ElevationDiff),
                new TypedValue((int)DxfCode.Real, data.Parapet),
                new TypedValue((int)DxfCode.Int16, data.UseInputHeight ? 1 : 0)
            );
        }

        private static void WriteBuildingMeta(BlockReference br, Transaction tr, BuildingParamData data)
        {
            if (br.ExtensionDictionary == ObjectId.Null)
            {
                br.CreateExtensionDictionary();
            }

            DBDictionary extDict = tr.GetObject(br.ExtensionDictionary, OpenMode.ForWrite) as DBDictionary;
            Xrecord rec;
            if (extDict.Contains(BuildingMetaDictName))
            {
                rec = tr.GetObject(extDict.GetAt(BuildingMetaDictName), OpenMode.ForWrite) as Xrecord;
            }
            else
            {
                rec = new Xrecord();
                extDict.SetAt(BuildingMetaDictName, rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }

            rec.Data = BuildBuildingMetaBuffer(data);
        }

        private static bool TryReadBuildingMeta(BlockReference br, Transaction tr, out BuildingParamData data)
        {
            data = default(BuildingParamData);
            if (br.ExtensionDictionary == ObjectId.Null) return false;

            DBDictionary extDict = tr.GetObject(br.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (extDict == null || !extDict.Contains(BuildingMetaDictName)) return false;

            Xrecord rec = tr.GetObject(extDict.GetAt(BuildingMetaDictName), OpenMode.ForRead) as Xrecord;
            if (rec == null || rec.Data == null) return false;

            ResultBuffer rb = rec.Data;
            TypedValue[] arr = rb.AsArray();
            if (arr == null || arr.Length < 8) return false;

            try
            {
                data.Height = Convert.ToDouble(arr[1].Value);
                data.Elevation = Convert.ToDouble(arr[2].Value);
                data.Floors = Convert.ToInt32(arr[3].Value);
                data.FloorHeight = Convert.ToDouble(arr[4].Value);
                data.ElevationDiff = Convert.ToDouble(arr[5].Value);
                data.Parapet = Convert.ToDouble(arr[6].Value);
                data.UseInputHeight = Convert.ToInt16(arr[7].Value) == 1;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private BuildingParamData BuildParamsFromUi(double elevation)
        {
            int floors = 0;
            int.TryParse(txtFloors?.Text, out floors);
            if (floors <= 0) floors = 18;

            double floorHeight = 2.9;
            if (!TryParseDouble(txtFloorHeight?.Text ?? string.Empty, out floorHeight) || floorHeight <= 0) floorHeight = 2.9;

            double elevDiff = 0.3;
            if (!TryParseDouble(txtElevationDiff?.Text ?? string.Empty, out elevDiff)) elevDiff = 0.3;

            double parapet = 1.2;
            if (!TryParseDouble(txtParapet?.Text ?? string.Empty, out parapet)) parapet = 1.2;

            return new BuildingParamData
            {
                Height = GetCalculatedThickness(),
                Elevation = elevation,
                Floors = floors,
                FloorHeight = floorHeight,
                ElevationDiff = elevDiff,
                Parapet = parapet,
                UseInputHeight = rbInputHeight != null && rbInputHeight.IsChecked == true
            };
        }

        private void ApplyUiFromBuildingMeta(BuildingParamData data)
        {
            if (_isSyncingBuildingParams) return;
            try
            {
                _isSyncingBuildingParams = true;
                if (rbInputHeight != null) rbInputHeight.IsChecked = data.UseInputHeight;
                if (rbCalcHeight != null) rbCalcHeight.IsChecked = !data.UseInputHeight;
                if (txtTotalHeight != null) txtTotalHeight.Text = data.Height.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                if (txtFloors != null) txtFloors.Text = data.Floors.ToString();
                if (txtFloorHeight != null) txtFloorHeight.Text = data.FloorHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                if (txtElevationDiff != null) txtElevationDiff.Text = data.ElevationDiff.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                if (txtParapet != null) txtParapet.Text = data.Parapet.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                if (txtStatus1 != null)
                {
                    txtStatus1.Text = $"已回填楼座参数：H={data.Height:F2}m, 底标高={data.Elevation:F2}m";
                    txtStatus1.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGreen);
                }
            }
            finally
            {
                _isSyncingBuildingParams = false;
            }
        }

        private void RefreshProvinceListByFilter(string filter)
        {
            if (_locationDB == null || cmbProvince == null) return;

            IEnumerable<string> source = _locationDB.Keys;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                source = source.Where(p => p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            _currentFilteredProvinces = source.ToList();
            string currentText = cmbProvince.Text;
            cmbProvince.Items.Clear();
            foreach (var p in _currentFilteredProvinces) cmbProvince.Items.Add(p);

            if (!string.IsNullOrWhiteSpace(currentText))
            {
                cmbProvince.Text = currentText;
            }
            if (cmbProvince.Items.Count > 0 && cmbProvince.SelectedItem == null)
            {
                string exact = _currentFilteredProvinces.FirstOrDefault(p => string.Equals(p, currentText, StringComparison.OrdinalIgnoreCase));
                cmbProvince.SelectedItem = exact ?? cmbProvince.Items[0];
            }
        }

        private string ResolveProvinceByInput()
        {
            if (_locationDB == null || _locationDB.Count == 0) return null;
            if (cmbProvince?.SelectedItem != null)
            {
                string selected = cmbProvince.SelectedItem.ToString();
                if (_locationDB.ContainsKey(selected)) return selected;
            }

            string text = cmbProvince?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return _locationDB.Keys.FirstOrDefault();

            string exact = _locationDB.Keys.FirstOrDefault(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact)) return exact;

            return _locationDB.Keys.FirstOrDefault(p => p.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool TryResolveProvinceForUi(out string province)
        {
            province = ResolveProvinceByInput();
            return !string.IsNullOrWhiteSpace(province)
                && _locationDB != null
                && _locationDB.ContainsKey(province);
        }

        private bool TryAutoMatchProvinceByCityInput(string cityInput)
        {
            if (_locationDB == null || cmbProvince == null) return false;
            if (string.IsNullOrWhiteSpace(cityInput)) return false;

            string keyword = cityInput.Trim();
            string exactProvince = null;
            string fuzzyProvince = null;

            foreach (var kv in _locationDB)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;

                if (exactProvince == null && kv.Value.Any(c => string.Equals(c.Name, keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    exactProvince = kv.Key;
                }

                if (fuzzyProvince == null && kv.Value.Any(c => c.Name != null && c.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    fuzzyProvince = kv.Key;
                }

                if (exactProvince != null && fuzzyProvince != null) break;
            }

            string matchedProvince = exactProvince ?? fuzzyProvince;
            if (string.IsNullOrWhiteSpace(matchedProvince)) return false;

            if (TryResolveProvinceForUi(out string currentProvince)
                && string.Equals(currentProvince, matchedProvince, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _isAutoMatchingProvinceByCity = true;
            try
            {
                if (!cmbProvince.Items.Cast<object>().Any(it => string.Equals(it?.ToString(), matchedProvince, StringComparison.OrdinalIgnoreCase)))
                {
                    RefreshProvinceListByFilter(string.Empty);
                }

                cmbProvince.SelectedItem = matchedProvince;
                cmbProvince.Text = matchedProvince;
                return true;
            }
            finally
            {
                _isAutoMatchingProvinceByCity = false;
            }
        }

        private static bool TryReadBuildingBlockParams(BlockReference br, Transaction tr, out double height, out double elevation)
        {
            height = 0;
            elevation = 0;
            if (br == null) return false;

            var bdef = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (bdef == null) return false;

            foreach (ObjectId subId in bdef)
            {
                Entity subEnt = tr.GetObject(subId, OpenMode.ForRead) as Entity;
                if (!(subEnt is Polyline subPoly)) continue;
                if (!(string.Equals(subPoly.Layer, "SUN_BUILD", StringComparison.OrdinalIgnoreCase) || subPoly.Thickness > 0)) continue;

                using (Polyline transformed = subPoly.Clone() as Polyline)
                {
                    transformed.TransformBy(br.BlockTransform);
                    height = transformed.Thickness;
                    elevation = transformed.Elevation;
                    return true;
                }
            }

            return false;
        }

        private void FillUiFromBuildingParams(double height, double elevation)
        {
            if (_isSyncingBuildingParams) return;

            try
            {
                _isSyncingBuildingParams = true;
                if (rbInputHeight != null) rbInputHeight.IsChecked = true;
                if (txtTotalHeight != null) txtTotalHeight.Text = height.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                if (txtStatus1 != null)
                {
                    txtStatus1.Text = $"已回填楼座参数：H={height:F2}m, 底标高={elevation:F2}m";
                    txtStatus1.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGreen);
                }
            }
            finally
            {
                _isSyncingBuildingParams = false;
            }
        }

        private void RefreshCityListByFilter()
        {
            if (_locationDB == null || cmbCity == null) return;
            string prov = ResolveProvinceByInput();
            if (string.IsNullOrWhiteSpace(prov)) return;
            if (!_locationDB.ContainsKey(prov)) return;

            string filter = cmbCity.Text ?? string.Empty;
            IEnumerable<CityData> source = _locationDB[prov];
            if (!string.IsNullOrWhiteSpace(filter))
            {
                source = source.Where(c => c.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            _currentFilteredCities = source.ToList();
            cmbCity.Items.Clear();
            foreach (var city in _currentFilteredCities) cmbCity.Items.Add(city.Name);
            if (cmbCity.Items.Count > 0)
            {
                string exact = _currentFilteredCities.Select(c => c.Name).FirstOrDefault(n => string.Equals(n, filter, StringComparison.OrdinalIgnoreCase));
                cmbCity.SelectedItem = exact ?? cmbCity.Items[0];
            }
            cmbCity.Text = filter;
        }

        private bool TryApplyUiParamsToSelectedBuilding()
        {
            if (_isSyncingBuildingParams || _isApplyingBuildingParams) return false;
            if (_selectedBuildingId == ObjectId.Null || _selectedBuildingId.IsErased) return false;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return false;

            double targetHeight;
            try
            {
                targetHeight = GetCalculatedThickness();
            }
            catch
            {
                return false;
            }

            string floorInfo = rbCalcHeight != null && rbCalcHeight.IsChecked == true
                ? $" / {txtFloors.Text}层"
                : string.Empty;

            bool changed = false;
            try
            {
                _isApplyingBuildingParams = true;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    if (_selectedBuildingId.IsErased)
                    {
                        tr.Commit();
                        return false;
                    }

                    BlockReference br = tr.GetObject(_selectedBuildingId, OpenMode.ForWrite, false) as BlockReference;
                    if (br == null || !IsPluginBuildingBlock(br, tr))
                    {
                        tr.Commit();
                        return false;
                    }

                    BlockTableRecord btrInfo = tr.GetObject(br.BlockTableRecord, OpenMode.ForWrite) as BlockTableRecord;
                    if (btrInfo == null)
                    {
                        tr.Commit();
                        return false;
                    }

                    BuildingParamData dataForMeta = default(BuildingParamData);
                    bool hasElevation = false;

                    foreach (ObjectId subId in btrInfo)
                    {
                        Entity subEnt = tr.GetObject(subId, OpenMode.ForWrite) as Entity;
                        if (subEnt is Polyline subPoly && (string.Equals(subPoly.Layer, "SUN_BUILD", StringComparison.OrdinalIgnoreCase) || subPoly.Thickness > 0))
                        {
                            if (!hasElevation)
                            {
                                dataForMeta = BuildParamsFromUi(subPoly.Elevation);
                                hasElevation = true;
                            }
                            if (Math.Abs(subPoly.Thickness - targetHeight) > 0.001)
                            {
                                subPoly.Thickness = targetHeight;
                                changed = true;
                            }
                        }
                        else if (subEnt is DBText txt && txt.TextString.StartsWith("H:"))
                        {
                            string newText = $"H:{targetHeight:F1}m{floorInfo}";
                            if (!string.Equals(txt.TextString, newText, StringComparison.Ordinal))
                            {
                                txt.TextString = newText;
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                    {
                        if (hasElevation) WriteBuildingMeta(br, tr, dataForMeta);
                        br.RecordGraphicsModified(true);
                    }

                    tr.Commit();
                }

                if (changed)
                {
                    RecalculateGlobalCacheSilent();
                    if (txtStatus1 != null)
                    {
                        txtStatus1.Text = $"已回写楼座参数：H={targetHeight:F2}m";
                        txtStatus1.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGreen);
                    }
                }

                return changed;
            }
            finally
            {
                _isApplyingBuildingParams = false;
            }
        }

        private void Realtime_ImpliedSelectionChanged(object sender, EventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            PromptSelectionResult psr;
            try { psr = doc.Editor.SelectImplied(); }
            catch { return; }

            if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count != 1)
            {
                _selectedBuildingId = ObjectId.Null;
                return;
            }

            ObjectId selectedId = psr.Value.GetObjectIds()[0];
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                Entity ent = tr.GetObject(selectedId, OpenMode.ForRead) as Entity;
                if (!(ent is BlockReference br) || !IsPluginBuildingBlock(br, tr))
                {
                    _selectedBuildingId = ObjectId.Null;
                    tr.Commit();
                    return;
                }

                _selectedBuildingId = selectedId;

                if (TryReadBuildingMeta(br, tr, out BuildingParamData meta))
                {
                    ApplyUiFromBuildingMeta(meta);
                }
                else if (TryReadBuildingBlockParams(br, tr, out double h, out double elev))
                {
                    FillUiFromBuildingParams(h, elev);
                }
                tr.Commit();
            }
        }

        private void Realtime_CommandWillStart(object sender, CommandEventArgs e)
        {
            string cmd = (e?.GlobalCommandName ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant();
            if (cmd == "QSAVE" || cmd == "SAVE" || cmd == "SAVEAS")
            {
                TrySaveDwgSettings(silent: true);
            }

            if (!IsWatchedRealtimeCommand(e.GlobalCommandName)) return;
            if (!_realtimeModeEnabled && !_facadeRealtimeEnabled) return;

            if (_realtimeModeEnabled)
            {
                var state = GetState();
                if (state == null || state.BaseGridCache == null || state.BaseGridCache.Count == 0) return;
            }

            _realtimeTrackingCommand = true;
            _realtimeDirtyIds.Clear();
            _realtimeNeedsFinalRecalc = false;
            _realtimeLastRecalcUtc = DateTime.MinValue;
            _realtimeAdaptiveThrottleMs = RealtimeThrottleMs;
            _realtimeRecalcPending = false;
            _realtimeEndCleanupPending = false;
            _realtimePreModifyBounds.Clear();

            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor?.WriteMessage("\n[RT] start command realtime: " + e.GlobalCommandName);
        }

        private void Realtime_CommandEnded(object sender, CommandEventArgs e)
        {
            string cmd = (e.GlobalCommandName ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant();
            if (cmd == "UNDO" || cmd == "U" || cmd == "REDO")
            {
                Realtime_ImpliedSelectionChanged(sender, EventArgs.Empty);
                return;
            }

            if (!IsWatchedRealtimeCommand(e.GlobalCommandName)) return;

            if (_facadeRealtimeEnabled && _facadeTargetIds.Count > 0)
            {
                RecalculateFacadeForTargets(_facadeTargetIds, silent: true);
            }

            if (!_realtimeModeEnabled)
            {
                _realtimeTrackingCommand = false;
                return;
            }

            if (!_realtimeTrackingCommand) return;

            _realtimeTrackingCommand = false;
            _realtimeEndCleanupPending = true;
            _realtimeNeedsFinalRecalc = true;

            // 命令结束后必须做一次最终全量校正，确保结果与最终位置一致。
            TryRealtimeRecalculate(force: true);
            if (!_realtimeRecalcRunning && !_realtimeRecalcPending)
            {
                CleanupRealtimeCommandState();
            }
        }

        private void chkFacadeRealtime_Checked(object sender, RoutedEventArgs e)
        {
            _facadeRealtimeEnabled = chkFacadeRealtime != null && chkFacadeRealtime.IsChecked == true;
            if (_facadeRealtimeEnabled && _facadeTargetIds.Count == 0 && txtStatus2 != null)
            {
                txtStatus2.Text = "立面实时联动已开启：请先执行一次立面测算并选定目标楼座";
                txtStatus2.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkBlue);
            }
        }

        private void CleanupRealtimeCommandState()
        {
            _realtimeDirtyIds.Clear();
            _realtimeNeedsFinalRecalc = false;
            _realtimeRecalcPending = false;
            _realtimeEndCleanupPending = false;
            _realtimePreModifyBounds.Clear();
        }

        private void Realtime_ObjectOpenedForModify(object sender, ObjectEventArgs e)
        {
            if (!_realtimeModeEnabled || !_realtimeTrackingCommand) return;

            Entity ent = e.DBObject as Entity;
            if (!IsRealtimeCandidateEntity(ent)) return;
            if (_realtimePreModifyBounds.ContainsKey(ent.ObjectId)) return;

            if (TryGetEntityBounds2D(ent, out BuildingBounds2D oldBounds))
            {
                _realtimePreModifyBounds[ent.ObjectId] = oldBounds;
            }
        }

        private void Realtime_ObjectModified(object sender, ObjectEventArgs e)
        {
            if (!_realtimeModeEnabled || !_realtimeTrackingCommand) return;

            Entity ent = e.DBObject as Entity;
            if (!IsRealtimeCandidateEntity(ent)) return;

            _realtimeDirtyIds.Add(ent.ObjectId);
            if (_realtimeRecalcRunning)
            {
                _realtimeRecalcPending = true;
                return;
            }
            TryRealtimeRecalculate(force: false);
        }

        private void Realtime_PointMonitor(object sender, PointMonitorEventArgs e)
        {
            if (!_realtimeModeEnabled || !_realtimeTrackingCommand) return;
            if (_realtimeDirtyIds.Count == 0) return;

            // MOVE/ROTATE 拖拽过程内用 PointMonitor 作为心跳，按节流推进实时重算。
            double elapsedMs = (DateTime.UtcNow - _realtimeLastRecalcUtc).TotalMilliseconds;
            if (elapsedMs < _realtimeAdaptiveThrottleMs) return;

            if (_realtimeRecalcRunning)
            {
                _realtimeRecalcPending = true;
                return;
            }

            TryRealtimeRecalculate(force: false);
        }

        private void TryRealtimeRecalculate(bool force)
        {
            if (_realtimeRecalcRunning)
            {
                _realtimeRecalcPending = true;
                if (force) _realtimeNeedsFinalRecalc = true;
                return;
            }
            if (!_realtimeModeEnabled && !force) return;

            HashSet<ObjectId> dirtySnapshot = null;

            if (!force)
            {
                if (_realtimeDirtyIds.Count == 0) return;
                double elapsedMs = (DateTime.UtcNow - _realtimeLastRecalcUtc).TotalMilliseconds;
                if (elapsedMs < _realtimeAdaptiveThrottleMs) return;
                dirtySnapshot = new HashSet<ObjectId>(_realtimeDirtyIds);
            }

            _realtimeRecalcRunning = true;
            var recalcSw = Stopwatch.StartNew();
            try
            {
                bool ok = true;
                if (force)
                {
                    RecalculateGlobalCacheSilent();
                    _realtimeDirtyIds.Clear();
                    _realtimePreModifyBounds.Clear();
                }
                else
                {
                    ok = RecalculateDirtyCacheSilent(dirtySnapshot);
                    if (ok)
                    {
                        foreach (ObjectId id in dirtySnapshot)
                        {
                            _realtimeDirtyIds.Remove(id);
                            _realtimePreModifyBounds.Remove(id);
                        }
                    }
                }

                if (!ok)
                {
                    _realtimeNeedsFinalRecalc = true;
                    return;
                }

                _realtimeLastRecalcUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _realtimeNeedsFinalRecalc = true;
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor?.WriteMessage("\n[RT] deferred: " + ex.Message);
            }
            finally
            {
                recalcSw.Stop();
                UpdateAdaptiveThrottle(recalcSw.ElapsedMilliseconds);
                _realtimeRecalcRunning = false;

                if (_realtimeRecalcPending)
                {
                    _realtimeRecalcPending = false;
                    bool forceNext = _realtimeNeedsFinalRecalc;
                    if (_realtimeModeEnabled && (forceNext || (_realtimeTrackingCommand && _realtimeDirtyIds.Count > 0)))
                    {
                        TryRealtimeRecalculate(force: forceNext);
                    }
                }

                if (_realtimeEndCleanupPending && !_realtimeRecalcRunning && !_realtimeRecalcPending)
                {
                    CleanupRealtimeCommandState();
                }
            }
        }

        private void UpdateAdaptiveThrottle(long elapsedMs)
        {
            if (elapsedMs <= 0) return;

            int next = _realtimeAdaptiveThrottleMs;
            if (elapsedMs > RealtimeTargetFrameMs + 30)
            {
                next += 15;
            }
            else if (elapsedMs < RealtimeTargetFrameMs - 25)
            {
                next -= 10;
            }

            if (next < RealtimeThrottleMinMs) next = RealtimeThrottleMinMs;
            if (next > RealtimeThrottleMaxMs) next = RealtimeThrottleMaxMs;
            _realtimeAdaptiveThrottleMs = next;

            if (PerfLogEnabled)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor?.WriteMessage($"\n[RT] frame={elapsedMs}ms throttle={_realtimeAdaptiveThrottleMs}ms");
            }
        }

        private bool RecalculateDirtyCacheSilent(HashSet<ObjectId> dirtyIds)
        {
            if (dirtyIds == null || dirtyIds.Count == 0) return true;

            var state = GetState();
            if (state == null || state.BaseGridCache.Count == 0 || state.GpuEngine == null) return false;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            var totalSw = Stopwatch.StartNew();
            var prepSw = Stopwatch.StartNew();

            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                TryBuildCurrentBuildingCache(tr, state, out List<SimpleBuilding> currentBldgCache, out List<BuildingBounds2D> currentBldgBounds);

                var dirtyBuildings = new List<SimpleBuilding>();
                foreach (ObjectId dirtyId in dirtyIds)
                {
                    if (dirtyId.IsNull || dirtyId.IsErased) continue;
                    Entity dirtyEnt = tr.GetObject(dirtyId, OpenMode.ForRead, false) as Entity;
                    if (!IsRealtimeCandidateEntity(dirtyEnt)) continue;
                    if (TryExtractBuilding(dirtyEnt, tr, out SimpleBuilding dirtyBldg))
                    {
                        dirtyBuildings.Add(dirtyBldg);
                    }
                }

                var dirtyBounds = new List<BuildingBounds2D>();
                if (dirtyBuildings.Count > 0)
                {
                    dirtyBounds.AddRange(BuildBuildingBounds(dirtyBuildings));
                }

                foreach (ObjectId dirtyId in dirtyIds)
                {
                    if (_realtimePreModifyBounds.TryGetValue(dirtyId, out BuildingBounds2D oldBounds))
                    {
                        dirtyBounds.Add(oldBounds);
                    }
                }

                if (dirtyBounds.Count == 0)
                {
                    tr.Commit();
                    return true;
                }

                if (!TryReadUiCalcParams(false, false, out UiCalcParams uiParams, out string parseError))
                {
                    doc.Editor.WriteMessage("\n[RT] 参数异常，延后全量重算: " + parseError);
                    return false;
                }

                int timeStep = uiParams.TimeStep;
                var fullRays = GetOrCreateSunRays(uiParams.Latitude, uiParams.IsWinter, uiParams.TimeStep);
                double dirtyMargin = Math.Max(0.1, uiParams.Spacing * 1.5);
                for (int i = 0; i < dirtyBounds.Count; i++)
                {
                    dirtyBounds[i] = ExpandBounds(dirtyBounds[i], dirtyMargin);
                }

                var dirtyIndices = new List<int>();
                var dirtyPts = new List<Point3d>();
                for (int i = 0; i < state.BaseGridCache.Count; i++)
                {
                    var center2d = new Point2d(state.BaseGridCache[i].Center.X, state.BaseGridCache[i].Center.Y);
                    if (!IsInsideAnyBounds(center2d, dirtyBounds)) continue;

                    dirtyIndices.Add(i);
                    dirtyPts.Add(state.BaseGridCache[i].Center);
                }

                prepSw.Stop();

                if (dirtyPts.Count == 0)
                {
                    tr.Commit();
                    return true;
                }

                var gpuSw = Stopwatch.StartNew();
                byte[] mask = state.GpuEngine.CalculateGridMask(dirtyPts, fullRays, currentBldgCache);
                gpuSw.Stop();

                var postSw = Stopwatch.StartNew();
                var affectedColors = new HashSet<short>();
                for (int local = 0; local < dirtyPts.Count; local++)
                {
                    int idx = dirtyIndices[local];
                    short oldColor = state.BaseGridCache[idx].IsInsideStatic
                        ? (short)(-1)
                        : GetColorIndex(state.BaseGridCache[idx].SurvivingRayIndices.Count * timeStep);
                    var surviving = new List<System.Numerics.Vector3>(fullRays.Count);
                    var survivingIndices = new List<int>(fullRays.Count);
                    for (int r = 0; r < fullRays.Count; r++)
                    {
                        if (mask[local * fullRays.Count + r] == 1)
                        {
                            surviving.Add(fullRays[r]);
                            survivingIndices.Add(r);
                        }
                    }

                    state.BaseGridCache[idx].SurvivingRays = surviving;
                    state.BaseGridCache[idx].SurvivingRayIndices = survivingIndices;

                    bool inBldg = false;
                    var center2d = new Point2d(state.BaseGridCache[idx].Center.X, state.BaseGridCache[idx].Center.Y);
                    for (int b = 0; b < currentBldgCache.Count; b++)
                    {
                        if (!currentBldgBounds[b].Contains(center2d)) continue;
                        if (IsPointInPolygon(center2d, currentBldgCache[b].Vertices)) { inBldg = true; break; }
                    }
                    state.BaseGridCache[idx].IsInsideStatic = inBldg;

                    short newColor = state.BaseGridCache[idx].IsInsideStatic
                        ? (short)(-1)
                        : GetColorIndex(state.BaseGridCache[idx].SurvivingRayIndices.Count * timeStep);
                    if (oldColor >= 0) affectedColors.Add(oldColor);
                    if (newColor >= 0) affectedColors.Add(newColor);
                }

                DrawMeshesToCAD(state, timeStep, tr, doc.Database, affectedColors);
                tr.Commit();
                postSw.Stop();
                totalSw.Stop();

                PerfLog(doc.Editor, "RecalculateDirty/Prepare", prepSw);
                PerfLog(doc.Editor, "RecalculateDirty/GPU", gpuSw);
                PerfLog(doc.Editor, "RecalculateDirty/Post", postSw);
                PerfLog(doc.Editor, "RecalculateDirty/Total", totalSw);
                return true;
            }
        }

        private void chkRealtimeMode_Checked(object sender, RoutedEventArgs e)
        {
            _realtimeModeEnabled = chkRealtimeMode != null && chkRealtimeMode.IsChecked == true;
            if (!_realtimeModeEnabled)
            {
                _realtimeTrackingCommand = false;
                _realtimeDirtyIds.Clear();
                _realtimeNeedsFinalRecalc = false;
                _realtimeRecalcPending = false;
                _realtimePreModifyBounds.Clear();
            }
        }

        private void cmbProvince_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_locationDB == null) return;
            if (_isAutoMatchingProvinceByCity) return;
            RefreshCityListByFilter();
        }

        private void cmbProvince_KeyUp(object sender, KeyEventArgs e)
        {
            if (cmbProvince == null) return;
            string typed = cmbProvince.Text ?? string.Empty;
            RefreshProvinceListByFilter(typed);
            cmbProvince.IsDropDownOpen = cmbProvince.Items.Count > 0;
            RefreshCityListByFilter();
        }

        private void cmbCity_KeyUp(object sender, KeyEventArgs e)
        {
            if (cmbCity == null) return;
            string typed = cmbCity.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(typed))
            {
                TryAutoMatchProvinceByCityInput(typed);
            }
            RefreshCityListByFilter();
            cmbCity.IsDropDownOpen = cmbCity.Items.Count > 0;
            cmbCity.Text = typed;
        }

        private void cmbCity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_locationDB == null || cmbCity == null) return;
            if (cmbCity.SelectedItem == null && string.IsNullOrWhiteSpace(cmbCity.Text)) return;
            if (!TryResolveProvinceForUi(out string prov)) return;

            bool isCustom = (prov == "自定义");
            txtLongitude.IsReadOnly = !isCustom;
            txtLatitude.IsReadOnly = !isCustom;
            txtLongitude.Background = isCustom ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 238, 238));
            txtLatitude.Background = isCustom ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 238, 238));

            // 【核心修复】：如果是读取图纸数据触发的事件，直接拦截，绝对不能覆盖存好的经纬度！
            if (_isSettingLoaded) return;

            var cityInfo = _locationDB[prov].FirstOrDefault(c => c.Name == cmbCity.Text || c.Name == cmbCity.SelectedItem?.ToString());
            if (cityInfo != null && !isCustom)
            {
                txtLongitude.Text = cityInfo.Lon.ToString("F2");
                txtLatitude.Text = cityInfo.Lat.ToString("F2");
            }
        }

        private void OnModeChanged(object sender, RoutedEventArgs e) { if (panelTotalHeight == null || panelCalcHeight == null) return; if (rbInputHeight.IsChecked == true) { panelTotalHeight.Visibility = System.Windows.Visibility.Visible; panelCalcHeight.Visibility = System.Windows.Visibility.Collapsed; } else { panelTotalHeight.Visibility = System.Windows.Visibility.Collapsed; panelCalcHeight.Visibility = System.Windows.Visibility.Visible; } }

        public double GetCalculatedThickness() { return rbInputHeight.IsChecked == true ? double.Parse(txtTotalHeight.Text) : (int.Parse(txtFloors.Text) * double.Parse(txtFloorHeight.Text)) + double.Parse(txtElevationDiff.Text) + double.Parse(txtParapet.Text); }

        // ==========================================
        // 完善保存与加载图纸设置 (加入全量参数与防覆盖机制)
        // ==========================================
        private bool TrySaveDwgSettings(bool silent)
        {
            if (_isAutoPersistingDwgSettings) return false;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return false;

            Database db = doc.Database;
            try
            {
                _isAutoPersistingDwgSettings = true;
                EnsureDefaultUiValues();

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
                    string dictName = "SUNLIGHT_PROJ_SETTINGS";
                    Xrecord xRec;
                    if (nod.Contains(dictName)) { xRec = (Xrecord)tr.GetObject(nod.GetAt(dictName), OpenMode.ForWrite); }
                    else { xRec = new Xrecord(); nod.SetAt(dictName, xRec); tr.AddNewlyCreatedDBObject(xRec, true); }

                    string province = ResolveProvinceByInput() ?? "上海";
                    string city = cmbCity?.Text;
                    if (string.IsNullOrWhiteSpace(city))
                    {
                        city = _locationDB != null && _locationDB.ContainsKey(province) && _locationDB[province].Count > 0
                            ? _locationDB[province][0].Name
                            : "上海";
                    }

                    ResultBuffer rb = new ResultBuffer(
                        new TypedValue((int)DxfCode.Text, txtLatitude?.Text ?? "31.23"),
                        new TypedValue((int)DxfCode.Text, txtTimeStep?.Text ?? DefaultTimeStepMinutesText),
                        new TypedValue((int)DxfCode.Text, txtGridSpacing?.Text ?? "2.0"),
                        new TypedValue((int)DxfCode.Text, province),
                        new TypedValue((int)DxfCode.Text, city),
                        new TypedValue((int)DxfCode.Text, txtCalcHeight?.Text ?? "0.9"),
                        new TypedValue((int)DxfCode.Text, txtLongitude?.Text ?? "121.47")
                    );
                    xRec.Data = rb;
                    tr.Commit();
                }

                if (!silent && txtStatus1 != null)
                {
                    txtStatus1.Text = "参数已永久保存至当前 DWG 文件！";
                }
                return true;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show("保存失败: " + ex.Message);
                return false;
            }
            finally
            {
                _isAutoPersistingDwgSettings = false;
            }
        }

        private bool TryLoadDwgSettings(bool silent)
        {
            if (_isAutoPersistingDwgSettings) return false;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return false;

            Database db = doc.Database;
            try
            {
                _isAutoPersistingDwgSettings = true;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                    string dictName = "SUNLIGHT_PROJ_SETTINGS";
                    if (nod.Contains(dictName))
                    {
                        Xrecord xRec = (Xrecord)tr.GetObject(nod.GetAt(dictName), OpenMode.ForRead);
                        ResultBuffer rb = xRec.Data;
                        if (rb != null)
                        {
                            var arr = rb.AsArray();
                            if (arr.Length >= 7)
                            {
                                _isSettingLoaded = true;
                                cmbProvince.SelectedItem = arr[3].Value.ToString();
                                cmbCity.SelectedItem = arr[4].Value.ToString();
                                txtLatitude.Text = arr[0].Value.ToString();
                                txtTimeStep.Text = arr[1].Value.ToString();
                                txtGridSpacing.Text = arr[2].Value.ToString();
                                txtCalcHeight.Text = arr[5].Value.ToString();
                                txtLongitude.Text = arr[6].Value.ToString();
                                _isSettingLoaded = false;
                            }
                        }

                        EnsureDefaultUiValues();
                        if (!silent && txtStatus1 != null) txtStatus1.Text = "已成功加载图纸专属设置！";
                    }
                    else
                    {
                        EnsureDefaultUiValues();
                        if (!silent) MessageBox.Show("当前图纸暂未保存过日照设置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                EnsureDefaultUiValues();
                if (!silent) MessageBox.Show("读取失败: " + ex.Message);
                return false;
            }
            finally
            {
                _isAutoPersistingDwgSettings = false;
            }
        }

        private void btnSaveDWG_Click(object sender, RoutedEventArgs e)
        {
            TrySaveDwgSettings(silent: false);
        }

        private void btnLoadDWG_Click(object sender, RoutedEventArgs e)
        {
            TryLoadDwgSettings(silent: false);
        }

        // ==========================================
        // 框选清理结果 (指哪打哪，不再全部误删)
        // ==========================================
        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var state = GetState();

            try
            {
                PromptSelectionOptions pso = new PromptSelectionOptions();
                pso.MessageForAdding = "\n请框选需要清理的日照结果网格(场地/立面) (右键确认): ";
                // 同时支持 SUN_RESULT 与 SUN_FACADE
                SelectionFilter filter = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                    new TypedValue((int)DxfCode.LayerName, "SUN_RESULT"),
                    new TypedValue((int)DxfCode.LayerName, "SUN_FACADE"),
                    new TypedValue((int)DxfCode.Operator, "OR>")
                });

                var psr = ed.GetSelection(pso, filter);
                if (psr.Status == PromptStatus.OK)
                {
                    using (DocumentLock docLock = doc.LockDocument())
                    using (Transaction tr = doc.TransactionManager.StartTransaction())
                    {
                        foreach (SelectedObject so in psr.Value)
                        {
                            Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                            ent.Erase();

                            // 如果删掉的网格刚好是当前激活范围里的，也从追踪名单里剔除
                            if (state != null && state.CurrentResultMeshes.Contains(so.ObjectId))
                            {
                                state.CurrentResultMeshes.Remove(so.ObjectId);
                                foreach (var kv in state.CurrentResultMeshByColor.ToList())
                                {
                                    if (kv.Value == so.ObjectId) state.CurrentResultMeshByColor.Remove(kv.Key);
                                }
                            }
                        }
                        tr.Commit();
                    }
                    txtStatus2.Text = $"已清理指定区域的日照网格。";
                }
            }
            catch (Exception ex) { MessageBox.Show($"清理图面异常:\n{ex.Message}"); }
        }

        private bool IsValidBuildingEntity(Entity ent, Transaction tr)
        {
            if (ent is Polyline poly) { return poly.Layer == "SUN_BUILD"; }
            else if (ent is BlockReference br)
            {
                BlockTableRecord btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                return btr.Name.StartsWith("Bldg_") || br.Layer == "SUN_BUILD";
            }
            return false;
        }

        public static bool TryExtractBuilding(Entity ent, Transaction tr, out SimpleBuilding building)
        {
            building = default;
            if (ent is Polyline p) { building = new SimpleBuilding { BaseZ = p.Elevation, TopZ = p.Elevation + p.Thickness, Vertices = GetPolyPoints(p) }; return true; }
            else if (ent is BlockReference br)
            {
                BlockTableRecord bdef = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                foreach (ObjectId subId in bdef.Cast<ObjectId>())
                {
                    Entity subEnt = tr.GetObject(subId, OpenMode.ForRead) as Entity;
                    if (subEnt is Polyline subPoly && (subPoly.Layer == "SUN_BUILD" || subPoly.Thickness > 0))
                    {
                        Polyline transformedPoly = subPoly.Clone() as Polyline;
                        transformedPoly.TransformBy(br.BlockTransform);
                        building = new SimpleBuilding { BaseZ = transformedPoly.Elevation, TopZ = transformedPoly.Elevation + transformedPoly.Thickness, Vertices = GetPolyPoints(transformedPoly) };
                        transformedPoly.Dispose();
                        return true;
                    }
                }
            }
            return false;
        }

        private void btnPickOutline_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            try
            {
                double h = GetCalculatedThickness();
                string floorInfo = rbCalcHeight.IsChecked == true ? $" / {txtFloors.Text}层" : "";

                PromptSelectionResult psr = ed.SelectImplied();
                List<ObjectId> idsToProcess = new List<ObjectId>();

                if (psr.Status == PromptStatus.OK && psr.Value.Count > 0)
                {
                    foreach (SelectedObject so in psr.Value) idsToProcess.Add(so.ObjectId);
                }
                else
                {
                    Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
                    while (true)
                    {
                        var peo = new PromptEntityOptions("\n请拾取拟建外轮廓 或 已生成的楼栋图块 [按ESC退出]: ");
                        peo.SetRejectMessage("\n只能选择多段线或程序的楼栋图块！");
                        peo.AddAllowedClass(typeof(Polyline), true);
                        peo.AddAllowedClass(typeof(BlockReference), true);
                        var per = ed.GetEntity(peo);
                        if (per.Status == PromptStatus.Cancel) break;
                        if (per.Status == PromptStatus.OK) idsToProcess.Add(per.ObjectId);
                    }
                }

                if (idsToProcess.Count == 0) return;

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    Database db = doc.Database;
                    LayerTable lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                    if (!lt.Has("SUN_BUILD")) { lt.UpgradeOpen(); LayerTableRecord ltr = new LayerTableRecord { Name = "SUN_BUILD", Color = Color.FromColorIndex(ColorMethod.ByAci, 3) }; lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true); }

                    foreach (ObjectId id in idsToProcess)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;

                        if (ent is Polyline poly)
                        {
                            Polyline extrudePoly = poly.Clone() as Polyline;
                            extrudePoly.Thickness = h;
                            extrudePoly.Layer = "SUN_BUILD";

                            BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                            string blockName = "Bldg_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                            BlockTableRecord btr = new BlockTableRecord { Name = blockName };
                            bt.UpgradeOpen(); bt.Add(btr); tr.AddNewlyCreatedDBObject(btr, true);

                            Extents3d ext = extrudePoly.GeometricExtents;
                            Point3d centroid = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, extrudePoly.Elevation);

                            extrudePoly.TransformBy(Matrix3d.Displacement(Point3d.Origin - centroid));
                            btr.AppendEntity(extrudePoly); tr.AddNewlyCreatedDBObject(extrudePoly, true);

                            DBText txtHeight = new DBText { TextString = $"H:{h:F1}m{floorInfo}", Height = 2.0, ColorIndex = 2, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = new Point3d(0, 1.5, 0) };
                            btr.AppendEntity(txtHeight); tr.AddNewlyCreatedDBObject(txtHeight, true);

                            DBText txtElev = new DBText { TextString = $"±{poly.Elevation:F2}m", Height = 2.0, ColorIndex = 3, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = new Point3d(0, -1.5, 0) };
                            btr.AppendEntity(txtElev); tr.AddNewlyCreatedDBObject(txtElev, true);

                            BlockReference br = new BlockReference(centroid, btr.ObjectId);
                            br.Layer = "SUN_BUILD";
                            BlockTableRecord ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                            ms.AppendEntity(br); tr.AddNewlyCreatedDBObject(br, true);
                            WriteBuildingMeta(br, tr, BuildParamsFromUi(poly.Elevation));

                            poly.Erase();
                        }
                        else if (ent is BlockReference br)
                        {
                            BlockTableRecord btrInfo = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                            if (btrInfo.Name.StartsWith("Bldg_"))
                            {
                                btrInfo.UpgradeOpen();
                                foreach (ObjectId subId in btrInfo.Cast<ObjectId>())
                                {
                                    Entity subEnt = tr.GetObject(subId, OpenMode.ForWrite) as Entity;
                                    if (subEnt is Polyline subPoly) { subPoly.Thickness = h; }
                                    else if (subEnt is DBText txt && txt.TextString.StartsWith("H:")) { txt.TextString = $"H:{h:F1}m{floorInfo}"; }
                                }
                                if (TryReadBuildingBlockParams(br, tr, out _, out double readElev))
                                {
                                    WriteBuildingMeta(br, tr, BuildParamsFromUi(readElev));
                                }
                                br.RecordGraphicsModified(true);
                            }
                        }
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex) { MessageBox.Show($"建模过程发生异常:\n{ex.Message}"); }
        }

        // ==========================================
        // 设定边界 (更换边界后，旧的成果网格被“脱钩”保留在图纸上)
        // ==========================================
        private void btnSetBoundary_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var state = GetState();
            if (state == null) return;

            try
            {
                var newBoundaries = CollectClosedBoundaries(doc, ed);
                if (newBoundaries.Count > 0)
                {
                    bool changed = state.BoundaryIds.Count != newBoundaries.Count
                        || state.BoundaryIds.Except(newBoundaries).Any();

                    if (changed)
                    {
                        state.BoundaryIds = newBoundaries;
                        state.BaseGridCache.Clear();
                        state.CurrentResultMeshes.Clear();
                        state.CurrentResultMeshByColor.Clear();
                    }

                    txtStatus1.Text = $"已设边界: {state.BoundaryIds.Count} 个，现状楼: {state.StaticBldgIds.Count} 栋";
                    txtStatus1.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                }
                else
                {
                    MessageBox.Show("未选中有效的闭合多段线边界。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { MessageBox.Show($"设置边界异常:\n{ex.Message}"); }
        }

        private void btnSelectStatic_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var state = GetState();
            if (state == null) return;

            try
            {
                SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Operator, "<OR"), new TypedValue((int)DxfCode.Start, "LWPOLYLINE"), new TypedValue((int)DxfCode.Start, "INSERT"), new TypedValue((int)DxfCode.Operator, "OR>") });

                PromptSelectionResult psr = ed.SelectImplied();
                if (psr.Status != PromptStatus.OK || psr.Value.Count == 0)
                {
                    Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
                    PromptSelectionOptions pso = new PromptSelectionOptions { MessageForAdding = "\n请框选参与遮挡计算的现状楼栋: " };
                    psr = ed.GetSelection(pso, filter);
                }

                if (psr.Status == PromptStatus.OK)
                {
                    state.StaticBldgIds.Clear();
                    using (Transaction tr = doc.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in psr.Value.GetObjectIds())
                        {
                            if (state.BoundaryIds.Contains(id)) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (IsValidBuildingEntity(ent, tr)) { state.StaticBldgIds.Add(id); }
                        }
                        tr.Commit();
                    }
                }
                txtStatus1.Text = $"已设边界，现状楼: {state.StaticBldgIds.Count} 栋"; txtStatus1.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }
            catch (Exception ex) { MessageBox.Show($"读取数据异常:\n{ex.Message}"); }
        }

        private void btnStep2_Click(object sender, RoutedEventArgs e)
        {
            var state = GetState();
            if (state == null || state.BoundaryIds == null || state.BoundaryIds.Count == 0) { MessageBox.Show("计算边界无效，请重新设定边界！"); return; }
            GenerateBaseGrid();
        }

        private void GenerateBaseGrid()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            var state = GetState();
            var totalSw = Stopwatch.StartNew();
            var prepSw = Stopwatch.StartNew();

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                if (!TryGetActiveBoundaries(tr, state, out List<Polyline> boundaries, out string boundaryError))
                {
                    MessageBox.Show(boundaryError);
                    return;
                }

                var activeBoundaryBounds = new List<BuildingBounds2D>();
                foreach (Polyline boundary in boundaries)
                {
                    if (TryGetEntityBounds2D(boundary, out BuildingBounds2D bounds))
                    {
                        activeBoundaryBounds.Add(bounds);
                    }
                }

                if (activeBoundaryBounds.Count > 0)
                {
                    ClearPlanResultsInBounds(tr, state, activeBoundaryBounds);
                }

                var bldgCache = new List<SimpleBuilding>(state.StaticBldgIds.Count);
                foreach (ObjectId id in state.StaticBldgIds)
                {
                    if (id.IsErased) continue;
                    if (TryExtractBuilding(tr.GetObject(id, OpenMode.ForRead) as Entity, tr, out SimpleBuilding bldg)) { bldgCache.Add(bldg); }
                }
                var bldgBounds = BuildBuildingBounds(bldgCache);

                if (!TryReadUiCalcParams(true, false, out UiCalcParams uiParams, out string parseError))
                {
                    MessageBox.Show(parseError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                LayerTable lt = tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (!lt.Has("SUN_RESULT")) { lt.UpgradeOpen(); LayerTableRecord ltr = new LayerTableRecord { Name = "SUN_RESULT" }; lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true); }

                int timeStep = uiParams.TimeStep;
                double spacing = uiParams.Spacing;
                var fullRays = GetOrCreateSunRays(uiParams.Latitude, uiParams.IsWinter, uiParams.TimeStep);
                state.TotalRaysGlobal = fullRays.Count;

                var boundaryInfos = new List<(List<Point2d> Points, Extents3d Ext, double CalcZ)>();
                int estimatedGridCount = 0;
                for (int i = 0; i < boundaries.Count; i++)
                {
                    var b = boundaries[i];
                    var ext = b.GeometricExtents;
                    int cols = Math.Max(1, (int)Math.Ceiling((ext.MaxPoint.X - ext.MinPoint.X) / spacing));
                    int rows = Math.Max(1, (int)Math.Ceiling((ext.MaxPoint.Y - ext.MinPoint.Y) / spacing));
                    estimatedGridCount += Math.Max(16, cols * rows);
                    boundaryInfos.Add((GetPolyPoints(b), ext, b.Elevation));
                }
                if (estimatedGridCount <= 0) estimatedGridCount = 16;

                List<Point3d> testPts = new List<Point3d>(estimatedGridCount);
                List<GridNode> tempNodes = new List<GridNode>(estimatedGridCount);
                state.BaseGridCache.RemoveAll(node =>
                {
                    var center2d = new Point2d(node.Center.X, node.Center.Y);
                    for (int i = 0; i < boundaryInfos.Count; i++)
                    {
                        if (IsPointInPolygon(center2d, boundaryInfos[i].Points)) return true;
                    }
                    return false;
                });

                var pointDedup = new HashSet<string>();
                foreach (var info in boundaryInfos)
                {
                    for (double x = info.Ext.MinPoint.X; x <= info.Ext.MaxPoint.X; x += spacing)
                    {
                        for (double y = info.Ext.MinPoint.Y; y <= info.Ext.MaxPoint.Y; y += spacing)
                        {
                            Point2d pt2d = new Point2d(x + spacing / 2, y + spacing / 2);
                            if (!IsPointInPolygon(pt2d, info.Points)) continue;

                            Point3d center = new Point3d(pt2d.X, pt2d.Y, info.CalcZ);
                            string key = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}|{1:F4}|{2:F4}", center.X, center.Y, center.Z);
                            if (!pointDedup.Add(key)) continue;

                            testPts.Add(center);
                            tempNodes.Add(new GridNode
                            {
                                NodeIndex = testPts.Count - 1,
                                Center = center,
                                Corners = new Point3d[]
                                {
                                    new Point3d(x, y, info.CalcZ),
                                    new Point3d(x + spacing, y, info.CalcZ),
                                    new Point3d(x + spacing, y + spacing, info.CalcZ),
                                    new Point3d(x, y + spacing, info.CalcZ)
                                }
                            });
                        }
                    }
                }
                prepSw.Stop();

                doc.Editor.WriteMessage($"\n[GPU] 计算 {testPts.Count} 阵位 (边界数: {boundaryInfos.Count})...");
                var gpuSw = Stopwatch.StartNew();
                byte[] mask = state.GpuEngine.CalculateGridMask(testPts, fullRays, bldgCache);
                gpuSw.Stop();

                var postSw = Stopwatch.StartNew();

                for (int i = 0; i < testPts.Count; i++)
                {
                    var survivingIndices = new List<int>(fullRays.Count);
                    for (int r = 0; r < fullRays.Count; r++) { if (mask[i * fullRays.Count + r] == 1) survivingIndices.Add(r); }
                    tempNodes[i].SurvivingRayIndices = survivingIndices;

                    bool inBldg = false;
                    var center2d = new Point2d(tempNodes[i].Center.X, tempNodes[i].Center.Y);
                    for (int b = 0; b < bldgCache.Count; b++)
                    {
                        if (!bldgBounds[b].Contains(center2d)) continue;
                        if (IsPointInPolygon(center2d, bldgCache[b].Vertices)) { inBldg = true; break; }
                    }
                    tempNodes[i].IsInsideStatic = inBldg;
                }

                state.BaseGridCache.AddRange(tempNodes);
                DrawMeshesToCAD(state, timeStep, tr, doc.Database);

                state.GpuEngine.BeginJig(testPts, fullRays);
                tr.Commit();
                postSw.Stop();
                totalSw.Stop();

                PerfLog(doc.Editor, "GenerateBaseGrid/Prepare", prepSw);
                PerfLog(doc.Editor, "GenerateBaseGrid/GPU", gpuSw);
                PerfLog(doc.Editor, "GenerateBaseGrid/Post", postSw);
                PerfLog(doc.Editor, "GenerateBaseGrid/Total", totalSw);

                txtStatus2.Text = $"当前计算范围缓存: {state.BaseGridCache.Count} 个网格"; txtStatus2.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }
        }

        private void btnStep3_Click(object sender, RoutedEventArgs e)
        {
            if (btnStep3 == null) return;

            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
            var state = GetState();
            bool hasPlanResult = state != null && state.BaseGridCache.Count > 0;
            bool hasFacadeResult = _facadeTargetIds.Count > 0;

            if (!hasPlanResult && !hasFacadeResult)
            {
                MessageBox.Show("请先完成场地或立面测算后再启动推敲。");
                btnStep3.IsChecked = false;
                return;
            }

            if (btnStep3.IsChecked == true)
            {
                btnStep3.Content = "■ 推敲中 (按 ESC 退出)"; btnStep3.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69));
                Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

                using (DocumentLock docLock = Application.DocumentManager.MdiActiveDocument.LockDocument())
                {
                    SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Operator, "<OR"), new TypedValue((int)DxfCode.Start, "LWPOLYLINE"), new TypedValue((int)DxfCode.Start, "INSERT"), new TypedValue((int)DxfCode.Operator, "OR>") });
                    PromptSelectionOptions pso = new PromptSelectionOptions { MessageForAdding = "\n请框选要拖拽的拟建楼栋群(支持多对象): " };
                    var psr = ed.GetSelection(pso, filter);

                    if (psr.Status == PromptStatus.OK && psr.Value.Count > 0)
                    {
                        var ppr = ed.GetPoint(new PromptPointOptions("\n请指定拖拽基点: "));
                        if (ppr.Status == PromptStatus.OK)
                        {
                            List<ObjectId> movingIds = new List<ObjectId>();
                            List<BuildingBounds2D> movingBounds = new List<BuildingBounds2D>();
                            foreach (SelectedObject so in psr.Value) movingIds.Add(so.ObjectId);

                            List<ObjectId> removedStaticIds = new List<ObjectId>();
                            foreach (ObjectId id in movingIds)
                            {
                                if (state.StaticBldgIds.Contains(id)) { state.StaticBldgIds.Remove(id); removedStaticIds.Add(id); }
                            }

                            // 推敲预览前先把移动楼从场地缓存影响里移除，避免原位阴影/网格残留。
                            if (hasPlanResult && removedStaticIds.Count > 0)
                            {
                                RecalculateGlobalCacheSilent();
                            }

                            PromptStatus dragStatus = PromptStatus.Cancel;
                            Point3d dragTargetPoint = ppr.Value;
                            bool hideCommitted = false;
                            SunlightJig jig = null;

                            try
                            {
                                // 事务1：隐藏原楼并清空原位结果，提交后再进入拖拽，避免“双楼座同显”。
                                using (Transaction trHide = Application.DocumentManager.MdiActiveDocument.TransactionManager.StartTransaction())
                                {
                                    Database db = Application.DocumentManager.MdiActiveDocument.Database;
                                    List<Entity> movingEnts = new List<Entity>();
                                    List<SimpleBuilding> bldgDataList = new List<SimpleBuilding>();
                                    var movingBuildingIndexById = new Dictionary<ObjectId, int>();
                                    List<SimpleBuilding> facadeStaticObstacles = null;
                                    List<SimpleBuilding> facadePreviewTargets = null;
                                    List<Tuple<int, int>> facadeMovingBindings = null;
                                    List<System.Numerics.Vector3> facadeRays = null;
                                    double facadeSpacing = 0.0;
                                    int jigTimeStep = 5;

                                    foreach (ObjectId id in movingIds)
                                    {
                                        Entity movingEnt = trHide.GetObject(id, OpenMode.ForWrite, false) as Entity;
                                        if (movingEnt == null || movingEnt.IsErased) continue;
                                        movingEnts.Add(movingEnt);
                                        if (TryGetEntityBounds2D(movingEnt, out BuildingBounds2D rawBounds)) movingBounds.Add(rawBounds);

                                        if (TryExtractBuilding(movingEnt, trHide, out SimpleBuilding bldgData))
                                        {
                                            movingBuildingIndexById[id] = bldgDataList.Count;
                                            bldgDataList.Add(bldgData);
                                        }
                                    }

                                    if (movingEnts.Count > 0)
                                    {
                                        if (!TryReadUiCalcParams(false, false, out UiCalcParams planParams, out string parseError))
                                        {
                                            MessageBox.Show(parseError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                            return;
                                        }
                                        jigTimeStep = planParams.TimeStep;

                                        if (hasPlanResult && movingBounds.Count > 0)
                                        {
                                            ClearPlanResultsInBounds(trHide, state, movingBounds);
                                        }

                                        if (hasFacadeResult && movingBounds.Count > 0)
                                        {
                                            ClearFacadeResultsForTargets(trHide, db, movingIds);
                                            ClearFacadeResultsInBounds(trHide, db, movingBounds);
                                        }

                                        foreach (var ent in movingEnts)
                                        {
                                            ent.Visible = false;
                                            ent.RecordGraphicsModified(true);
                                        }

                                        if (hasFacadeResult)
                                        {
                                            if (!TryReadUiCalcParams(true, false, out UiCalcParams facadeParams, out parseError))
                                            {
                                                MessageBox.Show(parseError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                                return;
                                            }

                                            jigTimeStep = facadeParams.TimeStep;
                                            facadeSpacing = facadeParams.Spacing;
                                            facadeRays = GetOrCreateSunRays(facadeParams.Latitude, facadeParams.IsWinter, facadeParams.TimeStep);

                                            var facadeTargetSet = new HashSet<ObjectId>(_facadeTargetIds.Where(id => !id.IsErased));
                                            facadeStaticObstacles = CollectFacadeObstacles(trHide, new HashSet<ObjectId>(movingIds));
                                            facadePreviewTargets = new List<SimpleBuilding>();
                                            facadeMovingBindings = new List<Tuple<int, int>>();

                                            foreach (ObjectId targetId in facadeTargetSet)
                                            {
                                                Entity targetEnt = trHide.GetObject(targetId, OpenMode.ForRead, false) as Entity;
                                                if (targetEnt == null || targetEnt.IsErased) continue;
                                                if (!TryExtractBuilding(targetEnt, trHide, out SimpleBuilding targetBldg)) continue;

                                                int targetIndex = facadePreviewTargets.Count;
                                                facadePreviewTargets.Add(targetBldg);
                                                if (movingBuildingIndexById.TryGetValue(targetId, out int movingIndex))
                                                {
                                                    facadeMovingBindings.Add(Tuple.Create(targetIndex, movingIndex));
                                                }
                                            }
                                        }

                                        jig = new SunlightJig(
                                            movingEnts,
                                            ppr.Value,
                                            hasPlanResult ? state.BaseGridCache : null,
                                            jigTimeStep,
                                            state != null ? state.TotalRaysGlobal : 0,
                                            state.GpuEngine,
                                            bldgDataList,
                                            facadeRays,
                                            facadeStaticObstacles,
                                            facadeSpacing,
                                            facadePreviewTargets,
                                            facadeMovingBindings);

                                        trHide.Commit();
                                        hideCommitted = true;
                                    }
                                }

                                if (hideCommitted)
                                {
                                    try { ed.SetImpliedSelection(new ObjectId[0]); } catch { }
                                    ed.Regen();

                                    string switchDiag;
                                    bool visualStyleChanged = TrySwitchToRealistic(ed, out switchDiag);
                                    ed.WriteMessage($"\n[VS] hook reached changed={visualStyleChanged} diag={switchDiag}");
                                    ed.WriteMessage($"\n[VS] enter-drag changed={visualStyleChanged} diag={switchDiag}");
                                    if (!visualStyleChanged)
                                    {
                                        ed.WriteMessage("\n[WARN] 未能执行真实样式切换命令。");
                                    }

                                    PromptResult res = ed.Drag(jig);
                                    dragStatus = res.Status;
                                    if (dragStatus == PromptStatus.OK)
                                    {
                                        dragTargetPoint = jig.CurrentPoint;
                                    }
                                }
                            }
                            finally
                            {
                                // 事务2：统一恢复原对象显示，并在确认落位后应用位移。
                                if (hideCommitted)
                                {
                                    using (Transaction trApply = Application.DocumentManager.MdiActiveDocument.TransactionManager.StartTransaction())
                                    {
                                        foreach (ObjectId id in movingIds)
                                        {
                                            Entity ent = trApply.GetObject(id, OpenMode.ForWrite, false) as Entity;
                                            if (ent == null || ent.IsErased) continue;
                                            ent.Visible = true;
                                            ent.RecordGraphicsModified(true);

                                            if (dragStatus == PromptStatus.OK)
                                            {
                                                Matrix3d disp = Matrix3d.Displacement(dragTargetPoint - ppr.Value);
                                                ent.TransformBy(disp);
                                            }
                                        }
                                        trApply.Commit();
                                    }
                                    ed.Regen();
                                }
                            }
                            foreach (ObjectId id in removedStaticIds) { state.StaticBldgIds.Add(id); }

                            if (hasFacadeResult)
                            {
                                RecalculateFacadeForTargets(_facadeTargetIds, silent: true);
                            }
                        }
                    }

                    if (hasPlanResult)
                    {
                        using (Transaction tr = Application.DocumentManager.MdiActiveDocument.TransactionManager.StartTransaction())
                        {
                            ForceResetPlanResultLayer(tr, Application.DocumentManager.MdiActiveDocument.Database, state);
                            tr.Commit();
                        }
                        ed.Regen();
                        RecalculateGlobalCacheSilent();
                    }
                    else
                    {
                        using (Transaction tr = Application.DocumentManager.MdiActiveDocument.TransactionManager.StartTransaction())
                        {
                            tr.Commit();
                        }
                        ed.Regen();
                    }
                }
                btnStep3.IsChecked = false;
                btnStep3.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
                UpdateSharedPushButtonLabel();
            }
        }

        // ==========================================
        // 核心渲染方法：只清理属于“本次计算范围”的网格
        // ==========================================
        private void DrawMeshesToCAD(ProjectState state, int timeStep, Transaction tr, Database db, HashSet<short> targetColors = null)
        {
            BlockTableRecord btr = tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
            bool fullRefresh = targetColors == null;

            if (fullRefresh)
            {
                foreach (ObjectId id in state.CurrentResultMeshes)
                {
                    if (id.IsErased) continue;
                    Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent != null) { ent.Erase(); }
                }
                state.CurrentResultMeshes.Clear();
                state.CurrentResultMeshByColor.Clear();
            }
            else
            {
                foreach (short color in targetColors)
                {
                    if (!state.CurrentResultMeshByColor.TryGetValue(color, out ObjectId meshId)) continue;
                    if (!meshId.IsErased)
                    {
                        Entity ent = tr.GetObject(meshId, OpenMode.ForWrite) as Entity;
                        if (ent != null) ent.Erase();
                    }
                    state.CurrentResultMeshes.Remove(meshId);
                    state.CurrentResultMeshByColor.Remove(color);
                }
            }

            var colorGroups = new Dictionary<short, List<GridNode>>();
            foreach (var node in state.BaseGridCache)
            {
                if (node.IsInsideStatic) continue;
                short color = GetColorIndex(node.SurvivingRayIndices.Count * timeStep);
                if (!fullRefresh && !targetColors.Contains(color)) continue;
                if (!colorGroups.ContainsKey(color)) colorGroups[color] = new List<GridNode>();
                colorGroups[color].Add(node);
            }

            foreach (var kvp in colorGroups)
            {
                if (kvp.Value.Count == 0) continue;
                Autodesk.AutoCAD.Geometry.Point3dCollection vertices = new Autodesk.AutoCAD.Geometry.Point3dCollection();
                Autodesk.AutoCAD.Geometry.Int32Collection faceArray = new Autodesk.AutoCAD.Geometry.Int32Collection();

                foreach (var node in kvp.Value)
                {
                    int vBase = vertices.Count;
                    vertices.Add(node.Corners[0]); vertices.Add(node.Corners[1]); vertices.Add(node.Corners[2]); vertices.Add(node.Corners[3]);
                    faceArray.Add(4); faceArray.Add(vBase); faceArray.Add(vBase + 1); faceArray.Add(vBase + 2); faceArray.Add(vBase + 3);
                }
                SubDMesh mesh = new SubDMesh(); mesh.SetDatabaseDefaults(); mesh.SetSubDMesh(vertices, faceArray, 0);
                mesh.ColorIndex = kvp.Key; mesh.Layer = "SUN_RESULT";

                // 将新生成的网格加入追踪名单
                ObjectId meshId = btr.AppendEntity(mesh);
                tr.AddNewlyCreatedDBObject(mesh, true);
                state.CurrentResultMeshes.Add(meshId);
                state.CurrentResultMeshByColor[kvp.Key] = meshId;
            }
        }

        private void RecalculateGlobalCacheSilent()
        {
            var state = GetState();
            if (state == null || state.BaseGridCache.Count == 0 || state.GpuEngine == null) return;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            var totalSw = Stopwatch.StartNew();
            var prepSw = Stopwatch.StartNew();

            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                TryBuildCurrentBuildingCache(tr, state, out List<SimpleBuilding> currentBldgCache, out List<BuildingBounds2D> currentBldgBounds);

                if (!TryReadUiCalcParams(false, false, out UiCalcParams uiParams, out string parseError))
                {
                    MessageBox.Show(parseError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int timeStep = uiParams.TimeStep;
                var fullRays = GetOrCreateSunRays(uiParams.Latitude, uiParams.IsWinter, uiParams.TimeStep);

                List<Point3d> pts = new List<Point3d>(state.BaseGridCache.Count);
                for (int i = 0; i < state.BaseGridCache.Count; i++) pts.Add(state.BaseGridCache[i].Center);
                prepSw.Stop();

                var gpuSw = Stopwatch.StartNew();
                byte[] mask = state.GpuEngine.CalculateGridMask(pts, fullRays, currentBldgCache);
                gpuSw.Stop();

                var postSw = Stopwatch.StartNew();

                for (int i = 0; i < pts.Count; i++)
                {
                    var surviving = new List<System.Numerics.Vector3>(fullRays.Count);
                    var survivingIndices = new List<int>(fullRays.Count);
                    for (int r = 0; r < fullRays.Count; r++)
                    {
                        if (mask[i * fullRays.Count + r] == 1)
                        {
                            surviving.Add(fullRays[r]);
                            survivingIndices.Add(r);
                        }
                    }

                    state.BaseGridCache[i].SurvivingRays = surviving;
                    state.BaseGridCache[i].SurvivingRayIndices = survivingIndices;

                    bool inBldg = false;
                    var center2d = new Point2d(state.BaseGridCache[i].Center.X, state.BaseGridCache[i].Center.Y);
                    for (int b = 0; b < currentBldgCache.Count; b++)
                    {
                        if (!currentBldgBounds[b].Contains(center2d)) continue;
                        if (IsPointInPolygon(center2d, currentBldgCache[b].Vertices)) { inBldg = true; break; }
                    }
                    state.BaseGridCache[i].IsInsideStatic = inBldg;
                }
                DrawMeshesToCAD(state, timeStep, tr, doc.Database);
                tr.Commit();
                postSw.Stop();
                totalSw.Stop();

                PerfLog(doc.Editor, "Recalculate/Prepare", prepSw);
                PerfLog(doc.Editor, "Recalculate/GPU", gpuSw);
                PerfLog(doc.Editor, "Recalculate/Post", postSw);
                PerfLog(doc.Editor, "Recalculate/Total", totalSw);
            }
        }

        public static short GetColorIndex(int sunMins) { if (sunMins < 60) return 1; if (sunMins < 120) return 2; if (sunMins < 180) return 3; if (sunMins < 240) return 4; if (sunMins < 300) return 5; if (sunMins < 360) return 6; if (sunMins < 420) return 8; return 250; }

        public static bool IsPointInPolygon(Point2d pt, List<Point2d> poly)
        {
            bool isInside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (((poly[i].Y > pt.Y) != (poly[j].Y > pt.Y)) && (pt.X < (poly[j].X - poly[i].X) * (pt.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X))
                    isInside = !isInside;
            }
            return isInside;
        }

        public static List<Point2d> GetPolyPoints(Polyline poly)
        {
            List<Point2d> pts = new List<Point2d>();
            for (int i = 0; i < poly.NumberOfVertices; i++)
            {
                if (poly.GetSegmentType(i) == SegmentType.Arc)
                {
                    CircularArc2d arc = poly.GetArcSegment2dAt(i);
                    Point2d[] samplePts = arc.GetSamplePoints(15);
                    foreach (var p in samplePts) pts.Add(p);
                }
                else { pts.Add(poly.GetPoint2dAt(i)); }
            }
            return pts;
        }

        private static double GetPolygonSignedArea(List<Point2d> vertices)
        {
            if (vertices == null || vertices.Count < 3) return 0;
            double area2 = 0;
            for (int i = 0; i < vertices.Count; i++)
            {
                Point2d a = vertices[i];
                Point2d b = vertices[(i + 1) % vertices.Count];
                area2 += (a.X * b.Y - b.X * a.Y);
            }
            return area2 * 0.5;
        }

        private static bool IsNorthForbidden(double nx, double ny)
        {
            const double cos45 = 0.7071067811865476;
            return ny >= cos45;
        }

        private static string ResolveFacadeSide(double nx, double ny)
        {
            if (ny <= -0.7071067811865476) return "S";
            if (nx >= 0) return "E";
            return "W";
        }

        private List<FacadeSampleCell> BuildFacadeSampleCells(SimpleBuilding building, double spacing, double outwardOffset, ObjectId targetId)
        {
            var cells = new List<FacadeSampleCell>();
            if (building.Vertices == null || building.Vertices.Count < 3) return cells;

            double safeSpacing = Math.Max(0.1, spacing);
            double h = Math.Max(0.1, building.TopZ - building.BaseZ);
            bool isCcw = GetPolygonSignedArea(building.Vertices) > 0;

            for (int i = 0; i < building.Vertices.Count; i++)
            {
                Point2d p0 = building.Vertices[i];
                Point2d p1 = building.Vertices[(i + 1) % building.Vertices.Count];
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) continue;

                double tx = dx / len;
                double ty = dy / len;
                double nx = isCcw ? ty : -ty;
                double ny = isCcw ? -tx : tx;
                if (IsNorthForbidden(nx, ny)) continue;

                int uCount = Math.Max(1, (int)Math.Ceiling(len / safeSpacing));
                int vCount = Math.Max(1, (int)Math.Ceiling(h / safeSpacing));
                double stepU = len / uCount;
                double stepV = h / vCount;
                string side = ResolveFacadeSide(nx, ny);

                for (int u = 0; u < uCount; u++)
                {
                    double u0 = u * stepU;
                    double u1 = (u + 1) * stepU;

                    for (int v = 0; v < vCount; v++)
                    {
                        double z0 = building.BaseZ + v * stepV;
                        double z1 = z0 + stepV;

                        Point3d c0 = new Point3d(p0.X + tx * u0 + nx * outwardOffset, p0.Y + ty * u0 + ny * outwardOffset, z0);
                        Point3d c1 = new Point3d(p0.X + tx * u1 + nx * outwardOffset, p0.Y + ty * u1 + ny * outwardOffset, z0);
                        Point3d c2 = new Point3d(p0.X + tx * u1 + nx * outwardOffset, p0.Y + ty * u1 + ny * outwardOffset, z1);
                        Point3d c3 = new Point3d(p0.X + tx * u0 + nx * outwardOffset, p0.Y + ty * u0 + ny * outwardOffset, z1);

                        cells.Add(new FacadeSampleCell
                        {
                            Side = side,
                            TargetId = targetId,
                            Center = new Point3d((c0.X + c1.X + c2.X + c3.X) * 0.25, (c0.Y + c1.Y + c2.Y + c3.Y) * 0.25, (c0.Z + c1.Z + c2.Z + c3.Z) * 0.25),
                            Corners = new[] { c0, c1, c2, c3 }
                        });
                    }
                }
            }

            return cells;
        }

        private void EnsureLayer(Transaction tr, Database db, string layerName, short colorIndex)
        {
            LayerTable lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null) return;

            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                LayerTableRecord ltrNew = new LayerTableRecord { Name = layerName, Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex) };
                lt.Add(ltrNew);
                tr.AddNewlyCreatedDBObject(ltrNew, true);
            }

            LayerTableRecord ltr = tr.GetObject(lt[layerName], OpenMode.ForWrite) as LayerTableRecord;
            if (ltr != null)
            {
                ltr.IsOff = false;
                ltr.IsFrozen = false;
                ltr.IsLocked = false;
            }
        }

        private void ClearLayerEntities(Transaction tr, Database db, string layerName)
        {
            BlockTableRecord ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return;

            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                {
                    ent.Erase();
                }
            }
        }

        private void ForceResetPlanResultLayer(Transaction tr, Database db, ProjectState state)
        {
            if (tr == null || db == null || state == null) return;
            EnsureLayer(tr, db, "SUN_RESULT", 3);
            ClearLayerEntities(tr, db, "SUN_RESULT");
            state.CurrentResultMeshes.Clear();
            state.CurrentResultMeshByColor.Clear();
        }

        private void ClearPlanResultsInBounds(Transaction tr, ProjectState state, IEnumerable<BuildingBounds2D> bounds)
        {
            if (tr == null || state == null || state.CurrentResultMeshes == null || bounds == null) return;
            var boundaryList = bounds.Where(b => b.MaxX >= b.MinX && b.MaxY >= b.MinY).ToList();
            if (boundaryList.Count == 0) return;

            foreach (ObjectId meshId in state.CurrentResultMeshes.ToList())
            {
                if (meshId.IsErased) continue;
                Entity ent = tr.GetObject(meshId, OpenMode.ForWrite, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (!TryGetEntityBounds2D(ent, out BuildingBounds2D meshBounds)) continue;
                if (!boundaryList.Any(b => b.Intersects(meshBounds))) continue;

                ent.Erase();
                state.CurrentResultMeshes.Remove(meshId);
                foreach (var kv in state.CurrentResultMeshByColor.ToList())
                {
                    if (kv.Value == meshId) state.CurrentResultMeshByColor.Remove(kv.Key);
                }
            }
        }

        private void ClearFacadeResultsInBounds(Transaction tr, Database db, IEnumerable<BuildingBounds2D> bounds)
        {
            if (tr == null || db == null || bounds == null) return;
            var boundaryList = bounds.Where(b => b.MaxX >= b.MinX && b.MaxY >= b.MinY).ToList();
            if (boundaryList.Count == 0) return;

            BlockTableRecord ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return;

            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (!string.Equals(ent.Layer, "SUN_FACADE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryGetEntityBounds2D(ent, out BuildingBounds2D meshBounds)) continue;
                if (!boundaryList.Any(b => b.Intersects(meshBounds))) continue;
                ent.Erase();
            }
        }

        private void EnsureRegApp(Transaction tr, Database db, string appName)
        {
            if (tr == null || db == null || string.IsNullOrWhiteSpace(appName)) return;
            RegAppTable regTable = tr.GetObject(db.RegAppTableId, OpenMode.ForRead) as RegAppTable;
            if (regTable == null || regTable.Has(appName)) return;

            regTable.UpgradeOpen();
            RegAppTableRecord rec = new RegAppTableRecord { Name = appName };
            regTable.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        private ResultBuffer BuildFacadeMetaBuffer(string targetHandle, string side, int serial)
        {
            return new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, FacadeMetaRegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, targetHandle ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, side ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, serial)
            );
        }

        private static bool TryGetFacadeMeta(Entity ent, out string targetHandle, out string side, out int serial)
        {
            targetHandle = null;
            side = null;
            serial = 0;
            if (ent == null || ent.IsErased) return false;

            ResultBuffer rb = ent.XData;
            if (rb == null) return false;
            TypedValue[] arr = rb.AsArray();
            if (arr == null || arr.Length == 0) return false;

            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i].TypeCode != (int)DxfCode.ExtendedDataRegAppName) continue;
                if (!string.Equals(arr[i].Value as string, FacadeMetaRegAppName, StringComparison.OrdinalIgnoreCase)) continue;

                string readHandle = null;
                string readSide = null;
                int readSerial = 0;

                for (int j = i + 1; j < arr.Length; j++)
                {
                    if (arr[j].TypeCode == (int)DxfCode.ExtendedDataRegAppName) break;

                    if (arr[j].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    {
                        if (readHandle == null) readHandle = arr[j].Value as string;
                        else if (readSide == null) readSide = arr[j].Value as string;
                    }
                    else if (arr[j].TypeCode == (int)DxfCode.ExtendedDataInteger16 || arr[j].TypeCode == (int)DxfCode.ExtendedDataInteger32)
                    {
                        try { readSerial = Convert.ToInt32(arr[j].Value); } catch { }
                    }
                }

                if (!string.IsNullOrWhiteSpace(readHandle) && !string.IsNullOrWhiteSpace(readSide))
                {
                    targetHandle = readHandle;
                    side = readSide;
                    serial = readSerial;
                    return true;
                }
            }

            return false;
        }

        private void ClearFacadeResultsForTargets(Transaction tr, Database db, IEnumerable<ObjectId> targetIds)
        {
            if (tr == null || db == null || targetIds == null) return;

            var targetHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in targetIds)
            {
                if (id == ObjectId.Null || id.IsErased) continue;
                targetHandles.Add(id.Handle.ToString());
            }
            if (targetHandles.Count == 0) return;

            BlockTableRecord ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return;

            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (!string.Equals(ent.Layer, "SUN_FACADE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryGetFacadeMeta(ent, out string targetHandle, out _, out _)) continue;
                if (!targetHandles.Contains(targetHandle)) continue;
                ent.Erase();
            }
        }

        private byte[] CalculateGridMaskChunked(SunlightCalculatorGPU gpu, List<Point3d> points, List<System.Numerics.Vector3> rays, List<SimpleBuilding> obstacles, int chunkSize = 12000)
        {
            if (gpu == null || points == null || rays == null || points.Count == 0 || rays.Count == 0) return new byte[0];
            if (points.Count <= chunkSize) return gpu.CalculateGridMask(points, rays, obstacles);

            int rayCount = rays.Count;
            byte[] merged = new byte[points.Count * rayCount];
            int writeOffset = 0;

            for (int start = 0; start < points.Count; start += chunkSize)
            {
                int count = Math.Min(chunkSize, points.Count - start);
                var chunk = points.GetRange(start, count);
                byte[] part = gpu.CalculateGridMask(chunk, rays, obstacles);
                if (part.Length != count * rayCount)
                {
                    return new byte[0];
                }

                Buffer.BlockCopy(part, 0, merged, writeOffset, part.Length);
                writeOffset += part.Length;
            }

            return merged;
        }

        private List<SimpleBuilding> CollectFacadeObstacles(Transaction tr, HashSet<ObjectId> targetIds)
        {
            var result = new List<SimpleBuilding>();
            Database db = Application.DocumentManager.MdiActiveDocument?.Database;
            if (db == null) return result;
            BlockTableRecord ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return result;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased || (targetIds != null && targetIds.Contains(id))) continue;
                Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (!(ent is Polyline) && !(ent is BlockReference)) continue;
                if (!string.Equals(ent.Layer, "SUN_BUILD", StringComparison.OrdinalIgnoreCase) && !(ent is BlockReference)) continue;

                if (TryExtractBuilding(ent, tr, out SimpleBuilding b)) result.Add(b);
            }

            return result;
        }

        private bool RecalculateFacadeForTargets(IEnumerable<ObjectId> targetIds, bool silent)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || targetIds == null) return false;

            HashSet<ObjectId> idSet = new HashSet<ObjectId>(targetIds.Where(id => id != ObjectId.Null && !id.IsErased));
            if (idSet.Count == 0) return false;

            try
            {
                if (!TryReadUiCalcParams(true, false, out UiCalcParams uiParams, out string parseError))
                {
                    if (!silent) MessageBox.Show(parseError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    var targets = new List<SimpleBuilding>();
                    var targetObjIds = new List<ObjectId>();
                    foreach (ObjectId id in idSet)
                    {
                        Entity selected = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (selected == null || selected.IsErased) continue;
                        if (TryExtractBuilding(selected, tr, out SimpleBuilding b))
                        {
                            targets.Add(b);
                            targetObjIds.Add(id);
                        }
                    }
                    if (targets.Count == 0)
                    {
                        if (!silent) MessageBox.Show("未能识别楼座几何，无法进行立面测算。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        return false;
                    }

                    // 多体块立面测算需要把目标楼之间的相互遮挡也纳入，不能整体排除目标集。
                    var obstacles = CollectFacadeObstacles(tr, null);
                    var rays = GetOrCreateSunRays(uiParams.Latitude, uiParams.IsWinter, uiParams.TimeStep);

                    double spacing = Math.Max(0.1, uiParams.Spacing);
                    double outwardOffset = Math.Max(0.05, spacing * 0.08);
                    var cells = new List<FacadeSampleCell>();
                    for (int i = 0; i < targets.Count; i++)
                    {
                        cells.AddRange(BuildFacadeSampleCells(targets[i], spacing, outwardOffset, targetObjIds[i]));
                    }

                    if (cells.Count == 0)
                    {
                        if (!silent) MessageBox.Show("立面网格生成失败：当前体块可能全部为北向或北偏45度内立面。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        return false;
                    }

                    var state = GetState();
                    var gpu = state?.GpuEngine ?? new SunlightCalculatorGPU();
                    List<Point3d> pointsOnly = cells.Select(c => c.Center).ToList();
                    byte[] mask = CalculateGridMaskChunked(gpu, pointsOnly, rays, obstacles);
                    if (mask.Length != pointsOnly.Count * Math.Max(1, rays.Count))
                    {
                        if (!silent) MessageBox.Show("立面测算失败：GPU结果长度异常，请减少单次选择量后重试。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    var targetBounds = new List<BuildingBounds2D>();
                    foreach (var target in targets)
                    {
                        if (target.Vertices == null || target.Vertices.Count == 0) continue;
                        double minX = target.Vertices.Min(p => p.X);
                        double maxX = target.Vertices.Max(p => p.X);
                        double minY = target.Vertices.Min(p => p.Y);
                        double maxY = target.Vertices.Max(p => p.Y);
                        targetBounds.Add(new BuildingBounds2D { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY });
                    }

                    EnsureLayer(tr, doc.Database, "SUN_FACADE", 6);
                    EnsureRegApp(tr, doc.Database, FacadeMetaRegAppName);
                    ClearFacadeResultsForTargets(tr, doc.Database, targetObjIds);
                    ClearFacadeResultsInBounds(tr, doc.Database, targetBounds);

                    BlockTableRecord ms = tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                    int rayCount = Math.Max(1, rays.Count);
                    int stepMin = Math.Max(1, uiParams.TimeStep);
                    var colorGroups = new Dictionary<Tuple<ObjectId, string, short>, List<FacadeSampleCell>>();
                    var sideSerialCounter = new Dictionary<Tuple<string, string>, int>();
                    var sideHourSum = new Dictionary<string, double> { { "E", 0 }, { "S", 0 }, { "W", 0 } };
                    var sideCount = new Dictionary<string, int> { { "E", 0 }, { "S", 0 }, { "W", 0 } };

                    for (int i = 0; i < cells.Count; i++)
                    {
                        int survive = 0;
                        for (int r = 0; r < rayCount; r++)
                        {
                            if (mask[i * rayCount + r] == 1) survive++;
                        }

                        int sunMins = survive * stepMin;
                        short color = GetColorIndex(sunMins);
                        var key = Tuple.Create(cells[i].TargetId, cells[i].Side, color);
                        if (!colorGroups.ContainsKey(key)) colorGroups[key] = new List<FacadeSampleCell>();
                        colorGroups[key].Add(cells[i]);

                        string side = cells[i].Side;
                        if (sideHourSum.ContainsKey(side))
                        {
                            sideHourSum[side] += sunMins / 60.0;
                            sideCount[side]++;
                        }
                    }

                    foreach (var kv in colorGroups)
                    {
                        Point3dCollection vertices = new Point3dCollection();
                        Int32Collection faceArray = new Int32Collection();

                        foreach (var cell in kv.Value)
                        {
                            int vBase = vertices.Count;
                            vertices.Add(cell.Corners[0]);
                            vertices.Add(cell.Corners[1]);
                            vertices.Add(cell.Corners[2]);
                            vertices.Add(cell.Corners[3]);
                            faceArray.Add(4);
                            faceArray.Add(vBase);
                            faceArray.Add(vBase + 1);
                            faceArray.Add(vBase + 2);
                            faceArray.Add(vBase + 3);
                        }

                        SubDMesh mesh = new SubDMesh();
                        mesh.SetDatabaseDefaults();
                        mesh.SetSubDMesh(vertices, faceArray, 0);
                        mesh.Layer = "SUN_FACADE";
                        mesh.ColorIndex = kv.Key.Item3;

                        string targetHandle = kv.Key.Item1.Handle.ToString();
                        string side = kv.Key.Item2;
                        var serialKey = Tuple.Create(targetHandle, side);
                        int serial = 1;
                        if (sideSerialCounter.TryGetValue(serialKey, out int existing))
                        {
                            serial = existing + 1;
                        }
                        sideSerialCounter[serialKey] = serial;
                        mesh.XData = BuildFacadeMetaBuffer(targetHandle, side, serial);

                        ms.AppendEntity(mesh);
                        tr.AddNewlyCreatedDBObject(mesh, true);
                    }

                    tr.Commit();

                    double eAvg = sideCount["E"] > 0 ? sideHourSum["E"] / sideCount["E"] : 0;
                    double sAvg = sideCount["S"] > 0 ? sideHourSum["S"] / sideCount["S"] : 0;
                    double wAvg = sideCount["W"] > 0 ? sideHourSum["W"] / sideCount["W"] : 0;
                    if (txtStatus2 != null)
                    {
                        txtStatus2.Text = $"立面测算完成: 面片{cells.Count} | E:{eAvg:F2}h S:{sAvg:F2}h W:{wAvg:F2}h";
                        txtStatus2.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkBlue);
                    }

                    _facadeTargetIds.Clear();
                    foreach (ObjectId id in targetObjIds) _facadeTargetIds.Add(id);

                    return true;
                }
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show("立面测算失败: " + ex.Message);
                return false;
            }
        }

        private bool TryExtractEntityAsBuilding(Entity ent, Transaction tr, out Polyline sourcePoly, out double sourceElevation)
        {
            sourcePoly = null;
            sourceElevation = 0;
            if (ent == null) return false;

            if (ent is Polyline p)
            {
                sourcePoly = p;
                sourceElevation = p.Elevation;
                return true;
            }

            if (!(ent is BlockReference br)) return false;
            BlockTableRecord bdef = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (bdef == null) return false;

            foreach (ObjectId subId in bdef)
            {
                Entity subEnt = tr.GetObject(subId, OpenMode.ForRead) as Entity;
                if (!(subEnt is Polyline subPoly)) continue;
                using (Polyline transformed = subPoly.Clone() as Polyline)
                {
                    transformed.TransformBy(br.BlockTransform);
                    sourcePoly = transformed.Clone() as Polyline;
                    sourceElevation = transformed.Elevation;
                    return sourcePoly != null;
                }
            }

            return false;
        }

        private void btnImportTz_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            try
            {
                Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
                var pso = new PromptSelectionOptions { MessageForAdding = "\n请选择要导入的天正日照对象（支持多段线/块）: " };
                SelectionFilter filter = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue((int)DxfCode.Start, "INSERT"),
                    new TypedValue((int)DxfCode.Operator, "OR>")
                });

                PromptSelectionResult psr = ed.SelectImplied();
                if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
                {
                    psr = ed.GetSelection(pso, filter);
                }
                if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0) return;

                double targetHeight = GetCalculatedThickness();
                int imported = 0;
                int skipped = 0;

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    Database db = doc.Database;
                    LayerTable lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                    if (!lt.Has("SUN_BUILD"))
                    {
                        lt.UpgradeOpen();
                        LayerTableRecord ltr = new LayerTableRecord { Name = "SUN_BUILD", Color = Color.FromColorIndex(ColorMethod.ByAci, 3) };
                        lt.Add(ltr);
                        tr.AddNewlyCreatedDBObject(ltr, true);
                    }

                    BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    BlockTableRecord ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                    foreach (ObjectId id in psr.Value.GetObjectIds())
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (!TryExtractEntityAsBuilding(ent, tr, out Polyline srcPoly, out double baseElev) || srcPoly == null)
                        {
                            skipped++;
                            continue;
                        }

                        srcPoly.Closed = true;
                        srcPoly.Layer = "SUN_BUILD";
                        srcPoly.Thickness = targetHeight;

                        string blockName = "Bldg_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        BlockTableRecord btr = new BlockTableRecord { Name = blockName };
                        bt.UpgradeOpen();
                        bt.Add(btr);
                        tr.AddNewlyCreatedDBObject(btr, true);

                        Extents3d ext = srcPoly.GeometricExtents;
                        Point3d centroid = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, srcPoly.Elevation);
                        srcPoly.TransformBy(Matrix3d.Displacement(Point3d.Origin - centroid));
                        btr.AppendEntity(srcPoly);
                        tr.AddNewlyCreatedDBObject(srcPoly, true);

                        DBText txtHeight = new DBText
                        {
                            TextString = $"H:{targetHeight:F1}m",
                            Height = 2.0,
                            ColorIndex = 2,
                            HorizontalMode = TextHorizontalMode.TextCenter,
                            VerticalMode = TextVerticalMode.TextVerticalMid,
                            AlignmentPoint = new Point3d(0, 1.5, 0)
                        };
                        btr.AppendEntity(txtHeight);
                        tr.AddNewlyCreatedDBObject(txtHeight, true);

                        DBText txtElev = new DBText
                        {
                            TextString = $"±{baseElev:F2}m",
                            Height = 2.0,
                            ColorIndex = 3,
                            HorizontalMode = TextHorizontalMode.TextCenter,
                            VerticalMode = TextVerticalMode.TextVerticalMid,
                            AlignmentPoint = new Point3d(0, -1.5, 0)
                        };
                        btr.AppendEntity(txtElev);
                        tr.AddNewlyCreatedDBObject(txtElev, true);

                        BlockReference br = new BlockReference(centroid, btr.ObjectId) { Layer = "SUN_BUILD" };
                        ms.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);

                        WriteBuildingMeta(br, tr, BuildParamsFromUi(baseElev));
                        imported++;
                    }

                    tr.Commit();
                }

                txtStatus1.Text = $"天正对象导入完成：成功 {imported}，跳过 {skipped}";
                txtStatus1.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGreen);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入天正对象失败: " + ex.Message);
            }
        }

        private void btnFacadeCalc_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            try
            {
                Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
                SelectionFilter filter = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue((int)DxfCode.Start, "INSERT"),
                    new TypedValue((int)DxfCode.Operator, "OR>")
                });

                PromptSelectionResult psr = ed.SelectImplied();
                if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
                {
                    PromptSelectionOptions pso = new PromptSelectionOptions
                    {
                        MessageForAdding = "\n请选择进行立面测算的楼座（支持多体块）: "
                    };
                    psr = ed.GetSelection(pso, filter);
                }

                if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0) return;
                RecalculateFacadeForTargets(psr.Value.GetObjectIds(), silent: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("立面测算失败: " + ex.Message);
            }
        }
    }
}