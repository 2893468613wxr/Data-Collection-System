namespace WpfApp22.Core.Buffers;

/// <summary>
/// 曲线降采样。
///
/// 痛点：屏幕只有约 1200 个像素宽，但缓冲区里可能有 20 万个点。
/// 如果直接等间隔抽样，尖峰（正好是报警最关心的异常）会被抽掉，
/// 因此这里采用"峰值保留"策略：每个桶输出该桶内的最小值和最大值各一个点。
/// </summary>
public static class Downsampler
{
    /// <summary>
    /// 峰值保留降采样。输出点数最多为 maxPoints（保证是偶数）。
    /// </summary>
    /// <returns>实际写入的输出点数。</returns>
    public static int PeakDecimate(
        double[] srcTimes,
        double[] srcValues,
        int srcCount,
        int maxPoints,
        double[] destTimes,
        double[] destValues)
    {
        if (srcCount <= maxPoints)
        {
            var n = Math.Min(srcCount, Math.Min(destTimes.Length, destValues.Length));
            Array.Copy(srcTimes, destTimes, n);
            Array.Copy(srcValues, destValues, n);
            return n;
        }

        if (maxPoints < 2) return 0;

        var bucketCount = maxPoints / 2;
        var outIndex = 0;
        var capacity = Math.Min(destTimes.Length, destValues.Length);

        for (var b = 0; b < bucketCount && outIndex + 1 < capacity; b++)
        {
            var from = (long)srcCount * b / bucketCount;
            var to = (long)srcCount * (b + 1) / bucketCount;
            if (to <= from) to = from + 1;

            var minIdx = (int)from;
            var maxIdx = (int)from;
            var min = srcValues[minIdx];
            var max = srcValues[minIdx];

            for (var i = (int)from + 1; i < to && i < srcCount; i++)
            {
                var v = srcValues[i];
                if (v < min) { min = v; minIdx = i; }
                if (v > max) { max = v; maxIdx = i; }
            }

            // 按时间先后输出，保证曲线不出现折返
            if (minIdx <= maxIdx)
            {
                destTimes[outIndex] = srcTimes[minIdx];
                destValues[outIndex++] = min;
                destTimes[outIndex] = srcTimes[maxIdx];
                destValues[outIndex++] = max;
            }
            else
            {
                destTimes[outIndex] = srcTimes[maxIdx];
                destValues[outIndex++] = max;
                destTimes[outIndex] = srcTimes[minIdx];
                destValues[outIndex++] = min;
            }
        }

        return outIndex;
    }

    /// <summary>等间隔抽样，用于导出时控制文件体积。</summary>
    public static int Uniform(double[] srcTimes, double[] srcValues, int srcCount, int maxPoints,
        double[] destTimes, double[] destValues)
    {
        if (srcCount <= maxPoints)
        {
            var n = Math.Min(srcCount, Math.Min(destTimes.Length, destValues.Length));
            Array.Copy(srcTimes, destTimes, n);
            Array.Copy(srcValues, destValues, n);
            return n;
        }

        var step = (double)srcCount / maxPoints;
        var count = 0;
        for (var i = 0; i < maxPoints; i++)
        {
            var idx = (int)(i * step);
            if (idx >= srcCount) break;
            destTimes[count] = srcTimes[idx];
            destValues[count] = srcValues[idx];
            count++;
        }

        // 保留最后一个点，避免时间窗尾部被截断
        if (count > 0 && count < maxPoints)
        {
            destTimes[count] = srcTimes[srcCount - 1];
            destValues[count] = srcValues[srcCount - 1];
            count++;
        }

        return count;
    }
}
