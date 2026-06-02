using System;
using System.Collections.Generic;
using System.Linq;
using ILGPU;
using ILGPU.Runtime;

namespace SunlightPlugin
{
    public struct Float2 { public float X; public float Y; }
    public struct Float3 { public float X; public float Y; public float Z; }

    public struct GPUBuilding
    {
        public float BaseZ; public float TopZ;
        public float MinX; public float MinY;
        public float MaxX; public float MaxY;
        public int VertexStartIndex; public int VertexCount;
    }

    public class SunlightCalculatorGPU : IDisposable
    {
        private Context _context;
        private Accelerator _accelerator;
        private Action<Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<GPUBuilding>, ArrayView<Float2>, ArrayView<byte>> _rayCastKernel;

        private MemoryBuffer1D<Float3, Stride1D.Dense> _jigPts;
        private MemoryBuffer1D<Float3, Stride1D.Dense> _jigRays;
        private MemoryBuffer1D<byte, Stride1D.Dense> _jigOutMask;

        public SunlightCalculatorGPU()
        {
            _context = Context.CreateDefault();
            _accelerator = _context.GetPreferredDevice(preferCPU: false).CreateAccelerator(_context);
            _rayCastKernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<GPUBuilding>, ArrayView<Float2>, ArrayView<byte>>(SunlightRayCastKernel);
        }

        public byte[] CalculateGridMask(List<Autodesk.AutoCAD.Geometry.Point3d> testPoints, List<System.Numerics.Vector3> sunRays, List<SimpleBuilding> buildings)
        {
            if (testPoints.Count == 0 || sunRays.Count == 0) return new byte[0];

            // 0栋建筑防崩溃短路阀 (修复 X0018 致命错误)
            if (buildings == null || buildings.Count == 0)
            {
                byte[] allSurvive = new byte[testPoints.Count * sunRays.Count];
                for (int i = 0; i < allSurvive.Length; i++) allSurvive[i] = 1;
                return allSurvive;
            }

            var bldgsGPU = new GPUBuilding[buildings.Count];
            var vertsGPU = new List<Float2>();
            for (int i = 0; i < buildings.Count; i++)
            {
                bldgsGPU[i].BaseZ = (float)buildings[i].BaseZ;
                bldgsGPU[i].TopZ = (float)buildings[i].TopZ;

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;

                bldgsGPU[i].VertexStartIndex = vertsGPU.Count;
                bldgsGPU[i].VertexCount = buildings[i].Vertices.Count;
                foreach (var v in buildings[i].Vertices)
                {
                    if (v.X < minX) minX = (float)v.X; if (v.X > maxX) maxX = (float)v.X;
                    if (v.Y < minY) minY = (float)v.Y; if (v.Y > maxY) maxY = (float)v.Y;
                    vertsGPU.Add(new Float2 { X = (float)v.X, Y = (float)v.Y });
                }
                bldgsGPU[i].MinX = minX; bldgsGPU[i].MinY = minY; bldgsGPU[i].MaxX = maxX; bldgsGPU[i].MaxY = maxY;
            }
            if (vertsGPU.Count == 0) vertsGPU.Add(new Float2 { X = 0, Y = 0 });

            var ptsGPU = new Float3[testPoints.Count];
            for (int i = 0; i < testPoints.Count; i++) ptsGPU[i] = new Float3 { X = (float)testPoints[i].X, Y = (float)testPoints[i].Y, Z = (float)testPoints[i].Z };

            var raysGPU = new Float3[sunRays.Count];
            for (int i = 0; i < sunRays.Count; i++) raysGPU[i] = new Float3 { X = sunRays[i].X, Y = sunRays[i].Y, Z = sunRays[i].Z };

            using (var devPts = _accelerator.Allocate1D(ptsGPU))
            using (var devRays = _accelerator.Allocate1D(raysGPU))
            using (var devBldgs = _accelerator.Allocate1D(bldgsGPU))
            using (var devVerts = _accelerator.Allocate1D(vertsGPU.ToArray()))
            using (var devOutMask = _accelerator.Allocate1D<byte>(ptsGPU.Length * raysGPU.Length))
            {
                _rayCastKernel((int)devPts.Length, devPts.View, devRays.View, devBldgs.View, devVerts.View, devOutMask.View);
                _accelerator.Synchronize();
                return devOutMask.GetAsArray1D();
            }
        }

        // 推敲模式：驻留显存预热
        public void BeginJig(List<Autodesk.AutoCAD.Geometry.Point3d> testPoints, List<System.Numerics.Vector3> sunRays)
        {
            EndJig();
            if (testPoints.Count == 0 || sunRays.Count == 0) return;
            var ptsGPU = testPoints.Select(p => new Float3 { X = (float)p.X, Y = (float)p.Y, Z = (float)p.Z }).ToArray();
            var raysGPU = sunRays.Select(r => new Float3 { X = r.X, Y = r.Y, Z = r.Z }).ToArray();
            _jigPts = _accelerator.Allocate1D(ptsGPU);
            _jigRays = _accelerator.Allocate1D(raysGPU);
            _jigOutMask = _accelerator.Allocate1D<byte>(ptsGPU.Length * raysGPU.Length);
        }

        // 推敲模式：每帧 1ms 极速计算增量遮挡
        // 推敲模式：每帧 1ms 极速计算增量遮挡 (已完美支持多栋建筑组团同时拖拽)
        public byte[] ComputeJigFrame(List<SimpleBuilding> movingBldgs)
        {
            if (_jigPts == null || movingBldgs == null || movingBldgs.Count == 0) return new byte[0];

            var bldgsGPU = new GPUBuilding[movingBldgs.Count];
            var vertsGPU = new List<Float2>();

            for (int i = 0; i < movingBldgs.Count; i++)
            {
                var bldg = movingBldgs[i];
                bldgsGPU[i].BaseZ = (float)bldg.BaseZ;
                bldgsGPU[i].TopZ = (float)bldg.TopZ;
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;

                bldgsGPU[i].VertexStartIndex = vertsGPU.Count;
                bldgsGPU[i].VertexCount = bldg.Vertices.Count;
                foreach (var v in bldg.Vertices)
                {
                    if (v.X < minX) minX = (float)v.X; if (v.X > maxX) maxX = (float)v.X;
                    if (v.Y < minY) minY = (float)v.Y; if (v.Y > maxY) maxY = (float)v.Y;
                    vertsGPU.Add(new Float2 { X = (float)v.X, Y = (float)v.Y });
                }
                bldgsGPU[i].MinX = minX; bldgsGPU[i].MinY = minY;
                bldgsGPU[i].MaxX = maxX; bldgsGPU[i].MaxY = maxY;
            }

            if (vertsGPU.Count == 0) vertsGPU.Add(new Float2 { X = 0, Y = 0 });

            using (var devBldgs = _accelerator.Allocate1D(bldgsGPU))
            using (var devVerts = _accelerator.Allocate1D(vertsGPU.ToArray()))
            {
                _rayCastKernel((int)_jigPts.Length, _jigPts.View, _jigRays.View, devBldgs.View, devVerts.View, _jigOutMask.View);
                _accelerator.Synchronize();
                return _jigOutMask.GetAsArray1D();
            }
        }

        public void EndJig()
        {
            _jigPts?.Dispose(); _jigPts = null;
            _jigRays?.Dispose(); _jigRays = null;
            _jigOutMask?.Dispose(); _jigOutMask = null;
        }

        // ILGPU 核函数：光线与包围盒及多边形的严格相交算法
        public static void SunlightRayCastKernel(Index1D index, ArrayView<Float3> testPoints, ArrayView<Float3> sunRays, ArrayView<GPUBuilding> buildings, ArrayView<Float2> vertices, ArrayView<byte> outputRayMask)
        {
            Float3 pt = testPoints[index];

            for (int r = 0; r < sunRays.Length; r++)
            {
                Float3 dir = sunRays[r];
                if (dir.Z <= 0) { outputRayMask[index * sunRays.Length + r] = 0; continue; }

                bool isBlocked = false;
                for (int b = 0; b < buildings.Length; b++)
                {
                    GPUBuilding bldg = buildings[b];

                    // AABB 包围盒快速拦截
                    float dx = dir.X; float dy = dir.Y;
                    float invDx = (dx > 1e-6f || dx < -1e-6f) ? 1.0f / dx : (dx >= 0 ? 1e6f : -1e6f);
                    float invDy = (dy > 1e-6f || dy < -1e-6f) ? 1.0f / dy : (dy >= 0 ? 1e6f : -1e6f);

                    float t1 = (bldg.MinX - pt.X) * invDx; float t2 = (bldg.MaxX - pt.X) * invDx;
                    float t3 = (bldg.MinY - pt.Y) * invDy; float t4 = (bldg.MaxY - pt.Y) * invDy;

                    float minX_t = t1 < t2 ? t1 : t2; float maxX_t = t1 > t2 ? t1 : t2;
                    float minY_t = t3 < t4 ? t3 : t4; float maxY_t = t3 > t4 ? t3 : t4;

                    float tmin = minX_t > minY_t ? minX_t : minY_t;
                    float tmax = maxX_t < maxY_t ? maxX_t : maxY_t;

                    if (tmax < 0.0f || tmin > tmax) continue;

                    // 精确多边形求交
                    for (int v = 0; v < bldg.VertexCount; v++)
                    {
                        int vIdx = bldg.VertexStartIndex + v;
                        int nextIdx = bldg.VertexStartIndex + ((v + 1) % bldg.VertexCount);

                        Float2 p1 = vertices[vIdx]; Float2 p2 = vertices[nextIdx];
                        float vx = p2.X - p1.X; float vy = p2.Y - p1.Y;
                        float wx = p1.X - pt.X; float wy = p1.Y - pt.Y;
                        float denominator = dy * vx - dx * vy;

                        if (denominator > 1e-6f || denominator < -1e-6f)
                        {
                            float u = (dx * wy - dy * wx) / denominator;
                            if (u >= 0.0f && u <= 1.0f)
                            {
                                float t = (wy * vx - wx * vy) / denominator;
                                if (t > 0.0f)
                                {
                                    float hitZ = pt.Z + t * dir.Z;
                                    if (hitZ >= bldg.BaseZ && hitZ <= bldg.TopZ) { isBlocked = true; break; }
                                }
                            }
                        }
                    }
                    if (isBlocked) break;
                }
                outputRayMask[index * sunRays.Length + r] = isBlocked ? (byte)0 : (byte)1;
            }
        }

        public void Dispose() { EndJig(); _accelerator?.Dispose(); _context?.Dispose(); }
    }
}