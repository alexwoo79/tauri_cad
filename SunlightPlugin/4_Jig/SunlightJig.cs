using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace SunlightPlugin
{
    public class SunlightJig : DrawJig
    {
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

        private struct FacadeCell
        {
            public Point3d Center;
            public Point3d[] Corners;
        }

        private Point3d _basePt, _currentPt;
        private List<Entity> _movingEnts;
        private List<SimpleBuilding> _movingBldgsOriginal;
        private readonly List<SimpleBuilding> _movingBldgsFrame;
        private readonly List<List<Point2d>> _movingVerticesFrame;
        private readonly List<BuildingBounds2D> _movingBoundsFrame;
        private List<GridNode> _baseGridCache;
        private SunlightCalculatorGPU _gpuEngine;
        private int _timeStep, _totalRaysCount;
        private double _influenceRadius;
        private double _bldgsMinX, _bldgsMaxX, _bldgsMinY, _bldgsMaxY;
        private Vector3d _lastGhostOffset = new Vector3d(0.0, 0.0, 0.0);

        private readonly Dictionary<short, List<GridNode>> _colorGroups = new Dictionary<short, List<GridNode>>();
        private readonly Dictionary<short, Point3dCollection> _groupPoints = new Dictionary<short, Point3dCollection>();
        private readonly Dictionary<short, IntegerCollection> _groupFaces = new Dictionary<short, IntegerCollection>();
        private readonly bool _facadeEnabled;
        private readonly bool _planEnabled;
        private readonly List<System.Numerics.Vector3> _facadeRays;
        private readonly List<SimpleBuilding> _facadeStaticObstacles;
        private readonly double _facadeSpacing;
        private readonly List<SimpleBuilding> _facadePreviewTargetsFrame;
        private readonly List<Tuple<int, int>> _facadeMovingBindings;

        private static readonly short[] _meshColors = new short[] { 1, 2, 3, 4, 5, 6, 8, 250 };

        // 记录拖拽前原始位置的包围盒 (用于抹除旧阴影)
        private double _origMinX, _origMaxX, _origMinY, _origMaxY;

        public SunlightJig(List<Entity> movingEnts, Point3d basePt, List<GridNode> baseGrid, int timeStep, int totalRaysCount, SunlightCalculatorGPU gpuEngine, List<SimpleBuilding> movingBldgsOriginal, List<System.Numerics.Vector3> facadeRays = null, List<SimpleBuilding> facadeStaticObstacles = null, double facadeSpacing = 0.0, List<SimpleBuilding> facadePreviewTargets = null, List<Tuple<int, int>> facadeMovingBindings = null)
        {
            _facadePreviewTargetsFrame = new List<SimpleBuilding>();
            _facadeMovingBindings = facadeMovingBindings ?? new List<Tuple<int, int>>();
            _movingEnts = new List<Entity>();
            foreach (var ent in movingEnts)
            {
                Entity clone = ent.Clone() as Entity;
                clone.ColorIndex = 5; // 群组变蓝
                _movingEnts.Add(clone);
            }

            _basePt = basePt; _currentPt = basePt;
            _baseGridCache = baseGrid ?? new List<GridNode>(); _timeStep = timeStep;
            _totalRaysCount = totalRaysCount; _gpuEngine = gpuEngine;
            _movingBldgsOriginal = movingBldgsOriginal;
            _facadeRays = facadeRays ?? new List<System.Numerics.Vector3>();
            _facadeStaticObstacles = facadeStaticObstacles ?? new List<SimpleBuilding>();
            _facadeSpacing = facadeSpacing;
            _facadeEnabled = _facadeRays.Count > 0 && _facadeSpacing > 0;
            _planEnabled = _baseGridCache.Count > 0;
            _movingBldgsFrame = new List<SimpleBuilding>(_movingBldgsOriginal.Count);
            _movingVerticesFrame = new List<List<Point2d>>(_movingBldgsOriginal.Count);
            _movingBoundsFrame = new List<BuildingBounds2D>(_movingBldgsOriginal.Count);

            // 【黑科技核心】：计算出这批楼栋的最高高度，乘以 3.5倍阴影系数，得到最大影响半径
            double maxZ = _movingBldgsOriginal.Count > 0 ? _movingBldgsOriginal.Max(b => b.TopZ - b.BaseZ) : 50.0;
            _influenceRadius = maxZ * 3.5 + 20.0; // 加 20 米的安全缓冲

            _bldgsMinX = double.MaxValue;
            _bldgsMinY = double.MaxValue;
            _bldgsMaxX = double.MinValue;
            _bldgsMaxY = double.MinValue;

            foreach (var originalBldg in _movingBldgsOriginal)
            {
                var srcVertices = originalBldg.Vertices;
                var movedVertices = new List<Point2d>(srcVertices.Count);
                double bMinX = double.MaxValue;
                double bMinY = double.MaxValue;
                double bMaxX = double.MinValue;
                double bMaxY = double.MinValue;

                for (int i = 0; i < srcVertices.Count; i++)
                {
                    var p = srcVertices[i];
                    if (p.X < _bldgsMinX) _bldgsMinX = p.X;
                    if (p.X > _bldgsMaxX) _bldgsMaxX = p.X;
                    if (p.Y < _bldgsMinY) _bldgsMinY = p.Y;
                    if (p.Y > _bldgsMaxY) _bldgsMaxY = p.Y;
                    if (p.X < bMinX) bMinX = p.X;
                    if (p.X > bMaxX) bMaxX = p.X;
                    if (p.Y < bMinY) bMinY = p.Y;
                    if (p.Y > bMaxY) bMaxY = p.Y;
                    movedVertices.Add(p);
                }

                _movingVerticesFrame.Add(movedVertices);
                _movingBldgsFrame.Add(new SimpleBuilding
                {
                    BaseZ = originalBldg.BaseZ,
                    TopZ = originalBldg.TopZ,
                    Vertices = movedVertices
                });
                _movingBoundsFrame.Add(new BuildingBounds2D { MinX = bMinX, MinY = bMinY, MaxX = bMaxX, MaxY = bMaxY });
            }

            if (_movingBldgsOriginal.Count == 0)
            {
                _bldgsMinX = _bldgsMaxX = 0.0;
                _bldgsMinY = _bldgsMaxY = 0.0;
            }

            var previewSource = facadePreviewTargets ?? _movingBldgsOriginal;
            for (int i = 0; i < previewSource.Count; i++)
            {
                var src = previewSource[i];
                _facadePreviewTargetsFrame.Add(new SimpleBuilding
                {
                    BaseZ = src.BaseZ,
                    TopZ = src.TopZ,
                    Vertices = src.Vertices != null ? new List<Point2d>(src.Vertices) : new List<Point2d>()
                });
            }

            // 锁定原始位置的最大外轮廓
            _origMinX = _bldgsMinX - _influenceRadius;
            _origMaxX = _bldgsMaxX + _influenceRadius;
            _origMinY = _bldgsMinY - _influenceRadius;
            _origMaxY = _bldgsMaxY + _influenceRadius;

            foreach (short c in _meshColors)
            {
                _colorGroups[c] = new List<GridNode>();
                _groupPoints[c] = new Point3dCollection();
                _groupFaces[c] = new IntegerCollection();
            }

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

        private List<FacadeCell> BuildFacadeCells()
        {
            var cells = new List<FacadeCell>();
            double safeSpacing = Math.Max(0.1, _facadeSpacing);
            double outwardOffset = Math.Max(0.05, safeSpacing * 0.08);

            for (int b = 0; b < _facadePreviewTargetsFrame.Count; b++)
            {
                var building = _facadePreviewTargetsFrame[b];
                if (building.Vertices == null || building.Vertices.Count < 3) continue;

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
                    // 推敲预览阶段临时展示四立面网格（含北向）。

                    int uCount = Math.Max(1, (int)Math.Ceiling(len / safeSpacing));
                    int vCount = Math.Max(1, (int)Math.Ceiling(h / safeSpacing));
                    double stepU = len / uCount;
                    double stepV = h / vCount;

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

                            cells.Add(new FacadeCell
                            {
                                Center = new Point3d((c0.X + c1.X + c2.X + c3.X) * 0.25, (c0.Y + c1.Y + c2.Y + c3.Y) * 0.25, (c0.Z + c1.Z + c2.Z + c3.Z) * 0.25),
                                Corners = new[] { c0, c1, c2, c3 }
                            });
                        }
                    }
                }
            }

            return cells;
        }

        private void DrawFacadePreview(WorldDraw draw)
        {
            var cells = BuildFacadeCells();
            if (cells.Count == 0) return;

            var obstacles = new List<SimpleBuilding>(_facadeStaticObstacles.Count + _movingBldgsFrame.Count);
            obstacles.AddRange(_facadeStaticObstacles);
            obstacles.AddRange(_movingBldgsFrame);

            var testPts = new List<Point3d>(cells.Count);
            for (int i = 0; i < cells.Count; i++) testPts.Add(cells[i].Center);

            byte[] mask = _gpuEngine.CalculateGridMask(testPts, _facadeRays, obstacles);
            if (mask.Length == 0) return;

            var facadeColorGroups = new Dictionary<short, List<FacadeCell>>();
            for (int i = 0; i < cells.Count; i++)
            {
                int survive = 0;
                for (int r = 0; r < _facadeRays.Count; r++)
                {
                    if (mask[i * _facadeRays.Count + r] == 1) survive++;
                }

                short color = SunControlUI.GetColorIndex(survive * _timeStep);
                if (!facadeColorGroups.ContainsKey(color)) facadeColorGroups[color] = new List<FacadeCell>();
                facadeColorGroups[color].Add(cells[i]);
            }

            foreach (var kv in facadeColorGroups)
            {
                Point3dCollection pts = new Point3dCollection();
                IntegerCollection faces = new IntegerCollection();
                foreach (var cell in kv.Value)
                {
                    int vBase = pts.Count;
                    pts.Add(cell.Corners[0]); pts.Add(cell.Corners[1]); pts.Add(cell.Corners[2]); pts.Add(cell.Corners[3]);
                    faces.Add(4); faces.Add(vBase); faces.Add(vBase + 1); faces.Add(vBase + 2); faces.Add(vBase + 3);
                }

                draw.SubEntityTraits.Color = kv.Key;
                draw.SubEntityTraits.FillType = FillType.FillAlways;
                draw.Geometry.Shell(pts, faces, null, null, null, false);
            }
        }

        public Point3d CurrentPoint => _currentPt;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var opt = new JigPromptPointOptions("\n拖拽进行组团实时推敲 (左键落位，ESC取消): ") { UserInputControls = UserInputControls.Accept3dCoordinates, BasePoint = _basePt, UseBasePoint = true };
            var res = prompts.AcquirePoint(opt);
            if (res.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            if (_currentPt.DistanceTo(res.Value) < 0.1) return SamplerStatus.NoChange;
            _currentPt = res.Value; return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            Vector3d offset = _currentPt - _basePt;

            // 1. 仅做增量变换，避免每帧克隆实体
            Vector3d ghostDelta = offset - _lastGhostOffset;
            if (ghostDelta.Length > 1e-9)
            {
                Matrix3d ghostDeltaMat = Matrix3d.Displacement(ghostDelta);
                foreach (var ent in _movingEnts)
                {
                    ent.TransformBy(ghostDeltaMat);
                }
                _lastGhostOffset = offset;
            }

            foreach (var ent in _movingEnts)
            {
                draw.Geometry.Draw(ent);
            }

            // 2. 实时包围盒：当前鼠标所在位置的影响范围
            double curMinX = _bldgsMinX + offset.X - _influenceRadius;
            double curMaxX = _bldgsMaxX + offset.X + _influenceRadius;
            double curMinY = _bldgsMinY + offset.Y - _influenceRadius;
            double curMaxY = _bldgsMaxY + offset.Y + _influenceRadius;

            // 3. 更新可复用的帧缓冲数据后送往 GPU
            for (int b = 0; b < _movingBldgsOriginal.Count; b++)
            {
                var originalBldg = _movingBldgsOriginal[b];
                var srcVertices = originalBldg.Vertices;
                var dstVertices = _movingVerticesFrame[b];
                bool countMismatch = dstVertices.Count != srcVertices.Count;
                if (countMismatch) dstVertices.Clear();

                double bMinX = double.MaxValue;
                double bMinY = double.MaxValue;
                double bMaxX = double.MinValue;
                double bMaxY = double.MinValue;

                for (int v = 0; v < srcVertices.Count; v++)
                {
                    var p = srcVertices[v];
                    var moved = new Point2d(p.X + offset.X, p.Y + offset.Y);
                    if (moved.X < bMinX) bMinX = moved.X;
                    if (moved.X > bMaxX) bMaxX = moved.X;
                    if (moved.Y < bMinY) bMinY = moved.Y;
                    if (moved.Y > bMaxY) bMaxY = moved.Y;

                    if (countMismatch) dstVertices.Add(moved);
                    else dstVertices[v] = moved;
                }

                _movingBoundsFrame[b] = new BuildingBounds2D { MinX = bMinX, MinY = bMinY, MaxX = bMaxX, MaxY = bMaxY };
            }

            if (_facadeEnabled && _facadePreviewTargetsFrame.Count > 0 && _facadeMovingBindings.Count > 0)
            {
                foreach (var bind in _facadeMovingBindings)
                {
                    if (bind == null) continue;
                    int targetIndex = bind.Item1;
                    int movingIndex = bind.Item2;
                    if (targetIndex < 0 || targetIndex >= _facadePreviewTargetsFrame.Count) continue;
                    if (movingIndex < 0 || movingIndex >= _movingBldgsFrame.Count) continue;

                    var moving = _movingBldgsFrame[movingIndex];
                    var target = _facadePreviewTargetsFrame[targetIndex];
                    target.BaseZ = moving.BaseZ;
                    target.TopZ = moving.TopZ;
                    target.Vertices = moving.Vertices;
                    _facadePreviewTargetsFrame[targetIndex] = target;
                }
            }

            if (_planEnabled)
            {
                byte[] movingMask = _gpuEngine.ComputeJigFrame(_movingBldgsFrame);
                if (movingMask.Length > 0)
                {
                    foreach (short c in _meshColors)
                    {
                        _colorGroups[c].Clear();
                    }

                    foreach (var node in _baseGridCache)
                    {
                        if (node.IsInsideStatic) continue;

                        bool inNewZone = node.Center.X >= curMinX && node.Center.X <= curMaxX && node.Center.Y >= curMinY && node.Center.Y <= curMaxY;
                        bool inOrigZone = node.Center.X >= _origMinX && node.Center.X <= _origMaxX && node.Center.Y >= _origMinY && node.Center.Y <= _origMaxY;

                        int rayCount = 0;
                        if (!inNewZone && !inOrigZone)
                        {
                            rayCount = node.SurvivingRayIndices.Count;
                        }
                        else
                        {
                            bool isInsideMoving = false;
                            var center2d = new Point2d(node.Center.X, node.Center.Y);
                            for (int b = 0; b < _movingBldgsFrame.Count; b++)
                            {
                                if (!_movingBoundsFrame[b].Contains(center2d)) continue;
                                if (SunControlUI.IsPointInPolygon(center2d, _movingBldgsFrame[b].Vertices))
                                {
                                    isInsideMoving = true; break;
                                }
                            }
                            if (isInsideMoving) continue;

                            foreach (int rIdx in node.SurvivingRayIndices)
                            {
                                if (movingMask[node.NodeIndex * _totalRaysCount + rIdx] == 1) rayCount++;
                            }
                        }

                        short color = SunControlUI.GetColorIndex(rayCount * _timeStep);
                        _colorGroups[color].Add(node);
                    }

                    foreach (short color in _meshColors)
                    {
                        var nodes = _colorGroups[color];
                        if (nodes.Count == 0) continue;

                        Point3dCollection pts = _groupPoints[color];
                        IntegerCollection faces = _groupFaces[color];
                        pts.Clear();
                        faces.Clear();

                        foreach (var node in nodes)
                        {
                            int vBase = pts.Count;
                            pts.Add(node.Corners[0]); pts.Add(node.Corners[1]); pts.Add(node.Corners[2]); pts.Add(node.Corners[3]);
                            faces.Add(4); faces.Add(vBase); faces.Add(vBase + 1); faces.Add(vBase + 2); faces.Add(vBase + 3);
                        }
                        draw.SubEntityTraits.Color = color; draw.SubEntityTraits.FillType = FillType.FillAlways;
                        draw.Geometry.Shell(pts, faces, null, null, null, false);
                    }
                }
            }

            if (_facadeEnabled)
            {
                DrawFacadePreview(draw);
            }
            return true;
        }

        ~SunlightJig()
        {
            if (_movingEnts != null) { foreach (var ent in _movingEnts) ent.Dispose(); }
        }
    }
}