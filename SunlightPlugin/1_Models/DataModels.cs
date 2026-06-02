using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace SunlightPlugin
{
    // 城市预设数据
    public class CityData
    {
        public string Name { get; set; }
        public double Lon { get; set; }
        public double Lat { get; set; }
    }

    // 日照网格节点数据
    public class GridNode
    {
        public Point3d[] Corners;
        public Point3d Center;
        public int NodeIndex; // 用于 GPU 数组极速寻址
        public List<int> SurvivingRayIndices; // 静态遮挡后幸存的光线编号
        public List<System.Numerics.Vector3> SurvivingRays;
        public bool IsInsideStatic; // 是否被建筑本体覆盖（隐身标记）
    }

    // 建筑简化几何体（必须是 struct，保障 ILGPU 显存连续性）
    public struct SimpleBuilding
    {
        public double BaseZ;
        public double TopZ;
        public List<Point2d> Vertices;
    }
}