// ============================================================================
// IoUCalculationTests.cs - IoU (Intersection over Union) 算法测试
// 
// 注意: CalculateIntersectionOverUnion 是 YoloDetector 的私有方法，
// 此测试通过复制核心算法逻辑来验证 IoU 计算的正确性。
// 如需直接测试，可将该方法改为 internal 并添加 InternalsVisibleTo。
// ============================================================================
using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class IoUCalculationTests
{
    /// <summary>
    /// 复制自 YoloNms.cs 的 IoU 计算逻辑，用于独立验证
    /// </summary>
    private static float CalculateIoU(float cx1, float cy1, float w1, float h1,
                                       float cx2, float cy2, float w2, float h2)
    {
        float x1_min = cx1 - w1 / 2;
        float y1_min = cy1 - h1 / 2;
        float x1_max = cx1 + w1 / 2;
        float y1_max = cy1 + h1 / 2;

        float x2_min = cx2 - w2 / 2;
        float y2_min = cy2 - h2 / 2;
        float x2_max = cx2 + w2 / 2;
        float y2_max = cy2 + h2 / 2;

        float left = Math.Max(x1_min, x2_min);
        float top = Math.Max(y1_min, y2_min);
        float right = Math.Min(x1_max, x2_max);
        float bottom = Math.Min(y1_max, y2_max);

        float intersectionArea = (left < right && top < bottom)
            ? (right - left) * (bottom - top)
            : 0;

        float area1 = w1 * h1;
        float area2 = w2 * h2;
        float unionArea = area1 + area2 - intersectionArea;

        return unionArea > 0 ? intersectionArea / unionArea : 0;
    }

    [Fact]
    public void IoU_完全重叠_返回1()
    {
        // 两个完全相同的框
        float iou = CalculateIoU(
            cx1: 100, cy1: 100, w1: 50, h1: 50,
            cx2: 100, cy2: 100, w2: 50, h2: 50
        );

        iou.Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void IoU_完全不重叠_返回0()
    {
        // 两个完全分离的框
        float iou = CalculateIoU(
            cx1: 50, cy1: 50, w1: 50, h1: 50,
            cx2: 200, cy2: 200, w2: 50, h2: 50
        );

        iou.Should().Be(0);
    }

    [Fact]
    public void IoU_部分重叠_返回正确值()
    {
        // Box1: center(100,100), size 100x100 → [50,50] to [150,150]
        // Box2: center(125,125), size 100x100 → [75,75] to [175,175]
        // Intersection: [75,75] to [150,150] = 75*75 = 5625
        // Area1 = Area2 = 10000
        // Union = 10000 + 10000 - 5625 = 14375
        // IoU = 5625 / 14375 ≈ 0.391

        float iou = CalculateIoU(
            cx1: 100, cy1: 100, w1: 100, h1: 100,
            cx2: 125, cy2: 125, w2: 100, h2: 100
        );

        iou.Should().BeApproximately(0.391f, 0.01f);
    }

    [Fact]
    public void IoU_一个框完全包含另一个_返回小框面积比()
    {
        // 大框: center(100,100), size 200x200
        // 小框: center(100,100), size 50x50 (完全被包含)
        // Intersection = 50*50 = 2500
        // Union = 40000 + 2500 - 2500 = 40000
        // IoU = 2500 / 40000 = 0.0625

        float iou = CalculateIoU(
            cx1: 100, cy1: 100, w1: 200, h1: 200,
            cx2: 100, cy2: 100, w2: 50, h2: 50
        );

        iou.Should().BeApproximately(0.0625f, 0.001f);
    }

    [Fact]
    public void IoU_边缘刚好接触_返回0()
    {
        // Box1: [0,0] to [100,100]
        // Box2: [100,0] to [200,100] (边缘接触但不重叠)
        float iou = CalculateIoU(
            cx1: 50, cy1: 50, w1: 100, h1: 100,
            cx2: 150, cy2: 50, w2: 100, h2: 100
        );

        iou.Should().Be(0);
    }

    [Theory]
    [InlineData(50, 0.14f)] // 50% 对角偏移 → 实际 IoU 约 0.14
    [InlineData(25, 0.39f)] // 25% 对角偏移 → 实际 IoU 约 0.39
    [InlineData(75, 0.03f)] // 75% 对角偏移 → 实际 IoU 约 0.03
    public void IoU_不同偏移量(float offset, float expectedIoU)
    {
        // 固定大小 100x100，只改变偏移
        float iou = CalculateIoU(
            cx1: 100, cy1: 100, w1: 100, h1: 100,
            cx2: 100 + offset, cy2: 100 + offset, w2: 100, h2: 100
        );

        iou.Should().BeApproximately(expectedIoU, 0.05f);
    }

    [Fact]
    public void IoU_零尺寸框_返回0()
    {
        float iou = CalculateIoU(
            cx1: 100, cy1: 100, w1: 0, h1: 0,
            cx2: 100, cy2: 100, w2: 50, h2: 50
        );

        iou.Should().Be(0);
    }
}
