using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        public ObjectId BoundaryId = ObjectId.Null;
        public List<ObjectId> StaticBldgIds = new List<ObjectId>();
        public List<GridNode> BaseGridCache = new List<GridNode>();

        // 【新增】：专门追踪当前激活边界所生成的日照网格，方便局部更新
        public List<ObjectId> CurrentResultMeshes = new List<ObjectId>();

        public SunlightCalculatorGPU GpuEngine = null;
        public int TotalRaysGlobal = 0;

        public ProjectState()
        {
            try { GpuEngine = new SunlightCalculatorGPU(); } catch { }
        }
    }

    public partial class SunControlUI : UserControl
    {
        private struct UiCalcParams
        {
            public int TimeStep;
            public double Latitude;
            public double Spacing;
            public double CalcZ;
            public bool IsWinter;
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
        }

        private Dictionary<Document, ProjectState> _states = new Dictionary<Document, ProjectState>();
        private Dictionary<string, List<CityData>> _locationDB;
        private bool _isSettingLoaded = false; // 【新增】：防止读取图纸设置时被事件覆盖的锁
        private string _sunRayCacheKey;
        private List<System.Numerics.Vector3> _sunRayCache;
        private const bool PerfLogEnabled = true;

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

        private bool TryReadUiCalcParams(bool needSpacing, out UiCalcParams parameters, out string error)
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
            if (txtCalcHeight != null && !string.IsNullOrWhiteSpace(txtCalcHeight.Text) && !TryParseDouble(txtCalcHeight.Text, out calcZ))
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

        public SunControlUI()
        {
            InitializeComponent();
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this)) return;

            try
            {
                _locationDB = CityDatabase.GetLocationDB();
                foreach (var prov in _locationDB.Keys) cmbProvince.Items.Add(prov);
                cmbProvince.SelectedIndex = 1;
            }
            catch { }

            Application.DocumentManager.DocumentToBeDestroyed += (s, e) =>
            {
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
            if (!_states.ContainsKey(doc)) _states[doc] = new ProjectState();
            return _states[doc];
        }

        private void cmbProvince_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbProvince.SelectedItem == null) return;
            cmbCity.Items.Clear();
            foreach (var city in _locationDB[cmbProvince.SelectedItem.ToString()]) cmbCity.Items.Add(city.Name);
            cmbCity.SelectedIndex = 0;
        }

        private void cmbCity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbCity.SelectedItem == null) return;
            string prov = cmbProvince.SelectedItem.ToString();
            bool isCustom = (prov == "自定义");
            txtLongitude.IsReadOnly = !isCustom;
            txtLatitude.IsReadOnly = !isCustom;
            txtLongitude.Background = isCustom ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 238, 238));
            txtLatitude.Background = isCustom ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 238, 238));

            // 【核心修复】：如果是读取图纸数据触发的事件，直接拦截，绝对不能覆盖存好的经纬度！
            if (_isSettingLoaded) return;

            var cityInfo = _locationDB[prov].FirstOrDefault(c => c.Name == cmbCity.SelectedItem.ToString());
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
        private void btnSaveDWG_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            try
            {
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
                    string dictName = "SUNLIGHT_PROJ_SETTINGS";
                    Xrecord xRec;
                    if (nod.Contains(dictName)) { xRec = (Xrecord)tr.GetObject(nod.GetAt(dictName), OpenMode.ForWrite); }
                    else { xRec = new Xrecord(); nod.SetAt(dictName, xRec); tr.AddNewlyCreatedDBObject(xRec, true); }

                    ResultBuffer rb = new ResultBuffer(
                        new TypedValue((int)DxfCode.Text, txtLatitude.Text),
                        new TypedValue((int)DxfCode.Text, txtTimeStep.Text),
                        new TypedValue((int)DxfCode.Text, txtGridSpacing.Text),
                        new TypedValue((int)DxfCode.Text, cmbProvince.SelectedItem?.ToString() ?? "上海"),
                        new TypedValue((int)DxfCode.Text, cmbCity.SelectedItem?.ToString() ?? "上海"),
                        new TypedValue((int)DxfCode.Text, txtCalcHeight.Text),
                        new TypedValue((int)DxfCode.Text, txtLongitude.Text)
                    );
                    xRec.Data = rb;
                    tr.Commit();
                }
                txtStatus1.Text = "参数已永久保存至当前 DWG 文件！";
            }
            catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message); }
        }

        private void btnLoadDWG_Click(object sender, RoutedEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            try
            {
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
                                _isSettingLoaded = true; // 上锁，阻止事件重置数据
                                cmbProvince.SelectedItem = arr[3].Value.ToString();
                                cmbCity.SelectedItem = arr[4].Value.ToString();
                                txtLatitude.Text = arr[0].Value.ToString();
                                txtTimeStep.Text = arr[1].Value.ToString();
                                txtGridSpacing.Text = arr[2].Value.ToString();
                                txtCalcHeight.Text = arr[5].Value.ToString();
                                txtLongitude.Text = arr[6].Value.ToString();
                                _isSettingLoaded = false; // 解锁
                            }
                        }
                        txtStatus1.Text = "已成功加载图纸专属设置！";
                    }
                    else { MessageBox.Show("当前图纸暂未保存过日照设置。"); }
                }
            }
            catch (Exception ex) { MessageBox.Show("读取失败: " + ex.Message); }
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
                pso.MessageForAdding = "\n请框选需要清理的日照色块网格 (右键确认): ";
                // 仅选中 SUN_RESULT 图层上的网格
                SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.LayerName, "SUN_RESULT") });

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
                ObjectId newBoundary = ObjectId.Null;
                PromptSelectionResult psr = ed.SelectImplied();
                if (psr.Status == PromptStatus.OK && psr.Value.Count > 0)
                {
                    foreach (SelectedObject so in psr.Value)
                    {
                        using (Transaction tr = doc.TransactionManager.StartTransaction())
                        {
                            if (tr.GetObject(so.ObjectId, OpenMode.ForRead) is Polyline) { newBoundary = so.ObjectId; break; }
                        }
                    }
                }

                if (newBoundary == ObjectId.Null)
                {
                    Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
                    using (DocumentLock docLock = doc.LockDocument())
                    {
                        var peo = new PromptEntityOptions("\n请选择日照计算边界(闭合多段线): "); peo.SetRejectMessage("\n只能选多段线!"); peo.AddAllowedClass(typeof(Polyline), true);
                        var resBound = ed.GetEntity(peo);
                        if (resBound.Status == PromptStatus.OK) { newBoundary = resBound.ObjectId; }
                    }
                }

                if (newBoundary != ObjectId.Null)
                {
                    // 【核心逻辑】：如果更换了新边界，把之前的网格管理权清空，它们将永远留在图纸上作为静态成果
                    if (state.BoundaryId != newBoundary)
                    {
                        state.BoundaryId = newBoundary;
                        state.BaseGridCache.Clear();
                        state.CurrentResultMeshes.Clear();
                    }
                    txtStatus1.Text = $"已设新边界，现状楼: {state.StaticBldgIds.Count} 栋"; txtStatus1.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
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
                            if (id == state.BoundaryId) continue;
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
            if (state == null || state.BoundaryId == ObjectId.Null || state.BoundaryId.IsErased) { MessageBox.Show("计算边界无效，请重新设定边界！"); return; }
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
                if (state.BoundaryId.IsErased) return;
                Polyline boundary = tr.GetObject(state.BoundaryId, OpenMode.ForRead) as Polyline;

                var bldgCache = new List<SimpleBuilding>(state.StaticBldgIds.Count);
                foreach (ObjectId id in state.StaticBldgIds)
                {
                    if (id.IsErased) continue;
                    if (TryExtractBuilding(tr.GetObject(id, OpenMode.ForRead) as Entity, tr, out SimpleBuilding bldg)) { bldgCache.Add(bldg); }
                }
                var bldgBounds = BuildBuildingBounds(bldgCache);

                if (!TryReadUiCalcParams(true, out UiCalcParams uiParams, out string parseError))
                {
                    MessageBox.Show(parseError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                LayerTable lt = tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (!lt.Has("SUN_RESULT")) { lt.UpgradeOpen(); LayerTableRecord ltr = new LayerTableRecord { Name = "SUN_RESULT" }; lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true); }

                int timeStep = uiParams.TimeStep;
                double spacing = uiParams.Spacing;
                double calcZ = uiParams.CalcZ;
                var fullRays = GetOrCreateSunRays(uiParams.Latitude, uiParams.IsWinter, uiParams.TimeStep);
                state.TotalRaysGlobal = fullRays.Count;

                var boundaryExt = boundary.GeometricExtents;
                int estimatedCols = Math.Max(1, (int)Math.Ceiling((boundaryExt.MaxPoint.X - boundaryExt.MinPoint.X) / spacing));
                int estimatedRows = Math.Max(1, (int)Math.Ceiling((boundaryExt.MaxPoint.Y - boundaryExt.MinPoint.Y) / spacing));
                int estimatedGridCount = Math.Max(16, estimatedCols * estimatedRows);

                List<Point3d> testPts = new List<Point3d>(estimatedGridCount);
                List<GridNode> tempNodes = new List<GridNode>(estimatedGridCount);
                List<Point2d> boundaryPts = GetPolyPoints(boundary);

                state.BaseGridCache.RemoveAll(node => IsPointInPolygon(new Point2d(node.Center.X, node.Center.Y), boundaryPts));

                for (double x = boundary.GeometricExtents.MinPoint.X; x <= boundary.GeometricExtents.MaxPoint.X; x += spacing)
                {
                    for (double y = boundary.GeometricExtents.MinPoint.Y; y <= boundary.GeometricExtents.MaxPoint.Y; y += spacing)
                    {
                        Point2d pt2d = new Point2d(x + spacing / 2, y + spacing / 2);
                        if (!IsPointInPolygon(pt2d, boundaryPts)) continue;

                        Point3d center = new Point3d(pt2d.X, pt2d.Y, calcZ);
                        testPts.Add(center);
                        tempNodes.Add(new GridNode { NodeIndex = testPts.Count - 1, Center = center, Corners = new Point3d[] { new Point3d(x, y, calcZ), new Point3d(x + spacing, y, calcZ), new Point3d(x + spacing, y + spacing, calcZ), new Point3d(x, y + spacing, calcZ) } });
                    }
                }
                prepSw.Stop();

                doc.Editor.WriteMessage($"\n[GPU] 计算 {testPts.Count} 阵位 (高度: {calcZ}m)...");
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
            Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
            var state = GetState();
            if (state == null || state.BaseGridCache.Count == 0) { MessageBox.Show("请先完成日照网格生成！"); btnStep3.IsChecked = false; return; }

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
                            foreach (SelectedObject so in psr.Value) movingIds.Add(so.ObjectId);

                            List<ObjectId> removedStaticIds = new List<ObjectId>();
                            foreach (ObjectId id in movingIds)
                            {
                                if (state.StaticBldgIds.Contains(id)) { state.StaticBldgIds.Remove(id); removedStaticIds.Add(id); }
                            }

                            using (Transaction tr = Application.DocumentManager.MdiActiveDocument.TransactionManager.StartTransaction())
                            {
                                LayerTable lt = tr.GetObject(Application.DocumentManager.MdiActiveDocument.Database.LayerTableId, OpenMode.ForRead) as LayerTable;
                                if (lt.Has("SUN_RESULT")) { ((LayerTableRecord)tr.GetObject(lt["SUN_RESULT"], OpenMode.ForWrite)).IsOff = true; }
                                tr.Commit();
                            }
                            ed.Regen();

                            if (removedStaticIds.Count > 0) { RecalculateGlobalCacheSilent(); }

                            using (Transaction tr = Application.DocumentManager.MdiActiveDocument.TransactionManager.StartTransaction())
                            {
                                List<Entity> movingEnts = new List<Entity>();
                                List<SimpleBuilding> bldgDataList = new List<SimpleBuilding>();

                                foreach (ObjectId id in movingIds)
                                {
                                    Entity movingEnt = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                                    if (TryExtractBuilding(movingEnt, tr, out SimpleBuilding bldgData))
                                    {
                                        movingEnts.Add(movingEnt); bldgDataList.Add(bldgData);
                                    }
                                }

                                if (movingEnts.Count > 0)
                                {
                                    if (!TryReadUiCalcParams(false, out UiCalcParams uiParams, out string parseError))
                                    {
                                        MessageBox.Show(parseError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                        return;
                                    }

                                    SunlightJig jig = new SunlightJig(movingEnts, ppr.Value, state.BaseGridCache, uiParams.TimeStep, state.TotalRaysGlobal, state.GpuEngine, bldgDataList);
                                    PromptResult res = ed.Drag(jig);
                                    if (res.Status == PromptStatus.OK)
                                    {
                                        Matrix3d disp = Matrix3d.Displacement(jig.CurrentPoint - ppr.Value);
                                        foreach (var ent in movingEnts) ent.TransformBy(disp);
                                    }
                                }
                                tr.Commit();
                            }
                            foreach (ObjectId id in removedStaticIds) { state.StaticBldgIds.Add(id); }
                        }
                    }

                    using (Transaction tr = Application.DocumentManager.MdiActiveDocument.TransactionManager.StartTransaction())
                    {
                        LayerTable lt = tr.GetObject(Application.DocumentManager.MdiActiveDocument.Database.LayerTableId, OpenMode.ForRead) as LayerTable;
                        if (lt.Has("SUN_RESULT")) { ((LayerTableRecord)tr.GetObject(lt["SUN_RESULT"], OpenMode.ForWrite)).IsOff = false; }
                        tr.Commit();
                    }
                    ed.Regen();
                    RecalculateGlobalCacheSilent();
                }
                btnStep3.IsChecked = false; btnStep3.Content = "④ 选取拟建楼开启推敲"; btnStep3.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
            }
        }

        // ==========================================
        // 核心渲染方法：只清理属于“本次计算范围”的网格
        // ==========================================
        private void DrawMeshesToCAD(ProjectState state, int timeStep, Transaction tr, Database db)
        {
            BlockTableRecord btr = tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

            // 仅清理本次追踪名单里的旧网格，绝对不碰图上其他的成果
            foreach (ObjectId id in state.CurrentResultMeshes)
            {
                if (id.IsErased) continue;
                Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                if (ent != null) { ent.Erase(); }
            }
            state.CurrentResultMeshes.Clear();

            var colorGroups = new Dictionary<short, List<GridNode>>();
            foreach (var node in state.BaseGridCache)
            {
                if (node.IsInsideStatic) continue;
                short color = GetColorIndex(node.SurvivingRayIndices.Count * timeStep);
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
            }
        }

        private void RecalculateGlobalCacheSilent()
        {
            var state = GetState();
            if (state == null || state.BaseGridCache.Count == 0 || state.GpuEngine == null) return;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            var totalSw = Stopwatch.StartNew();
            var prepSw = Stopwatch.StartNew();

            var currentBldgCache = new List<SimpleBuilding>(state.StaticBldgIds.Count);
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in state.StaticBldgIds)
                {
                    if (id.IsErased) continue;
                    if (TryExtractBuilding(tr.GetObject(id, OpenMode.ForRead) as Entity, tr, out SimpleBuilding bldg)) { currentBldgCache.Add(bldg); }
                }
                var currentBldgBounds = BuildBuildingBounds(currentBldgCache);

                if (!TryReadUiCalcParams(false, out UiCalcParams uiParams, out string parseError))
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
    }
}