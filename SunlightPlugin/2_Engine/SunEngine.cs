using System;
using System.Collections.Generic;

namespace SunlightPlugin
{
    public static class SunEngine
    {
        // 生成特定纬度下，大寒或冬至的太阳射线向量列表
        public static List<System.Numerics.Vector3> GenerateSunRays(double latitude, bool isWinterSolstice, int timeStepMinutes)
        {
            int safeStepMinutes = timeStepMinutes > 0 ? timeStepMinutes : 60;
            double latRad = latitude * Math.PI / 180.0;

            // 赤纬角：冬至 -23.45度，大寒约 -20.26度
            double declination = isWinterSolstice ? -23.45 : -20.26;
            double decRad = declination * Math.PI / 180.0;

            // 分析时段：冬至 9:00-15:00，大寒 8:00-16:00
            double startHour = isWinterSolstice ? 9.0 : 8.0;
            double endHour = isWinterSolstice ? 15.0 : 16.0;
            int startMinute = (int)Math.Round(startHour * 60.0);
            int endMinute = (int)Math.Round(endHour * 60.0);

            int estimatedRayCount = Math.Max(1, ((endMinute - startMinute) / safeStepMinutes) + 1);
            List<System.Numerics.Vector3> rays = new List<System.Numerics.Vector3>(estimatedRayCount);

            for (int minute = startMinute; minute <= endMinute; minute += safeStepMinutes)
            {
                double h = minute / 60.0;
                double timeAngle = (h - 12.0) * 15.0; // 时角：中午12点为0度，每小时15度
                double timeAngleRad = timeAngle * Math.PI / 180.0;

                // 计算太阳高度角(Altitude)和方位角(Azimuth)
                double sinAlt = Math.Sin(latRad) * Math.Sin(decRad) + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(timeAngleRad);
                double altRad = Math.Asin(sinAlt);
                if (altRad <= 0) continue; // 太阳在地平线以下

                double cosAzi = (Math.Sin(altRad) * Math.Sin(latRad) - Math.Sin(decRad)) / (Math.Cos(altRad) * Math.Cos(latRad));
                cosAzi = Math.Max(-1.0, Math.Min(1.0, cosAzi));
                double aziRad = Math.Acos(cosAzi);
                if (h < 12.0) aziRad = 2 * Math.PI - aziRad; // 上午方位角修正

                // 转换为三维方向向量 (Z轴朝上，Y轴朝北)
                float dx = (float)(Math.Cos(altRad) * Math.Sin(aziRad));
                float dy = (float)(-Math.Cos(altRad) * Math.Cos(aziRad));
                float dz = (float)Math.Sin(altRad);

                rays.Add(new System.Numerics.Vector3(dx, dy, dz));
            }
            return rays;
        }
    }
}