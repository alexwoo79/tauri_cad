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

        private static readonly short[] _meshColors = new short[] { 1, 2, 3, 4, 5, 6, 8, 250 };

        // 记录拖拽前原始位置的包围盒 (用于抹除旧阴影)
        private double _origMinX, _origMaxX, _origMinY, _origMaxY;

        public SunlightJig(List<Entity> movingEnts, Point3d basePt, List<GridNode> baseGrid, int timeStep, int totalRaysCount, SunlightCalculatorGPU gpuEngine, List<SimpleBuilding> movingBldgsOriginal)
        {
            _movingEnts = new List<Entity>();
            foreach (var ent in movingEnts)
            {
                Entity clone = ent.Clone() as Entity;
                clone.ColorIndex = 5; // 群组变蓝
                _movingEnts.Add(clone);
            }

            _basePt = basePt; _currentPt = basePt;
            _baseGridCache = baseGrid; _timeStep = timeStep;
            _totalRaysCount = totalRaysCount; _gpuEngine = gpuEngine;
            _movingBldgsOriginal = movingBldgsOriginal;
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

            // 锁定原始位置的最大外轮廓
            _origMinX = _bldgsMinX - _influenceRadius;
            _origMaxX = _bldgsMaxX + _influenceRadius;
            _origMinY = _bldgsMinY - _influenceRadius;
            _origMaxY = _bldgsMaxY + _influenceRadius;
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

            // 1. 同时绘制多个拖拽中的幽灵模型
            foreach (var ent in _movingEnts)
            {
                Entity ghostEnt = ent.Clone() as Entity;
                ghostEnt.TransformBy(Matrix3d.Displacement(offset));
                draw.Geometry.Draw(ghostEnt);
                ghostEnt.Dispose();
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

            byte[] movingMask = _gpuEngine.ComputeJigFrame(_movingBldgsFrame);
            if (movingMask.Length == 0) return true;

            var colorGroups = new Dictionary<short, List<GridNode>>();
            foreach (short c in _meshColors) colorGroups[c] = new List<GridNode>();

            // 4. 【全图渲染 + 局部算力剔除】：保持全图可见，但只算影响范围内的网格
            foreach (var node in _baseGridCache)
            {
                if (node.IsInsideStatic) continue;

                bool inNewZone = node.Center.X >= curMinX && node.Center.X <= curMaxX && node.Center.Y >= curMinY && node.Center.Y <= curMaxY;
                bool inOrigZone = node.Center.X >= _origMinX && node.Center.X <= _origMaxX && node.Center.Y >= _origMinY && node.Center.Y <= _origMaxY;

                int rayCount = 0;

                // 如果不仅不在新影子里，也不在旧影子里 -> 属于【范围外】
                if (!inNewZone && !inOrigZone)
                {
                    // 范围外：直接复用原本的计算结果，跳过 GPU 检查，极速渲染！
                    rayCount = node.SurvivingRayIndices.Count;
                }
                else
                {
                    // 属于【范围内】：精确计算
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
                    // 如果网格被拖拽的楼体正好压住，就不画网格
                    if (isInsideMoving) continue;

                    // 获取 GPU 给出的最新遮挡情况
                    foreach (int rIdx in node.SurvivingRayIndices)
                    {
                        if (movingMask[node.NodeIndex * _totalRaysCount + rIdx] == 1) rayCount++;
                    }
                }

                short color = SunControlUI.GetColorIndex(rayCount * _timeStep);
                colorGroups[color].Add(node);
            }

            // 5. 将整个全图的网格一起送入显卡渲染队列
            foreach (var kvp in colorGroups)
            {
                if (kvp.Value.Count == 0) continue;
                Point3dCollection pts = new Point3dCollection();
                IntegerCollection faces = new IntegerCollection();
                foreach (var node in kvp.Value)
                {
                    int vBase = pts.Count;
                    pts.Add(node.Corners[0]); pts.Add(node.Corners[1]); pts.Add(node.Corners[2]); pts.Add(node.Corners[3]);
                    faces.Add(4); faces.Add(vBase); faces.Add(vBase + 1); faces.Add(vBase + 2); faces.Add(vBase + 3);
                }
                draw.SubEntityTraits.Color = kvp.Key; draw.SubEntityTraits.FillType = FillType.FillAlways;
                draw.Geometry.Shell(pts, faces, null, null, null, false);
            }
            return true;
        }

        ~SunlightJig()
        {
            if (_movingEnts != null) { foreach (var ent in _movingEnts) ent.Dispose(); }
        }
    }
}