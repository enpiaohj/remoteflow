using Xunit;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// QualityGradeEvaluator 纯逻辑回归：只测打分规则，不触网、不弹 UI。
/// </summary>
public sealed class QualityGradeEvaluatorTests
{
    [Fact]
    public void 优秀_低延迟低抖动低丢包()
    {
        Assert.Equal(QualityLevel.Excellent,
            QualityGradeEvaluator.Evaluate(rttMs: 20, jitterMs: 5, lossPct: 0, reconnectCount: 0));
    }

    [Fact]
    public void 良好_中等延迟或轻微丢包()
    {
        Assert.Equal(QualityLevel.Good,
            QualityGradeEvaluator.Evaluate(rttMs: 100, jitterMs: 20, lossPct: 2, reconnectCount: 0));
    }

    [Fact]
    public void 一般_丢包偏高()
    {
        Assert.Equal(QualityLevel.Fair,
            QualityGradeEvaluator.Evaluate(rttMs: 50, jitterMs: 10, lossPct: 6, reconnectCount: 0));
    }

    [Fact]
    public void 较差_高延迟()
    {
        Assert.Equal(QualityLevel.Poor,
            QualityGradeEvaluator.Evaluate(rttMs: 500, jitterMs: 10, lossPct: 0, reconnectCount: 0));
    }

    [Fact]
    public void 无任何指标_未知()
    {
        Assert.Equal(QualityLevel.Unknown,
            QualityGradeEvaluator.Evaluate(rttMs: null, jitterMs: null, lossPct: null, reconnectCount: 0));
    }

    [Fact]
    public void 重连次数3次以上_即使指标优秀也落较差()
    {
        Assert.Equal(QualityLevel.Poor,
            QualityGradeEvaluator.Evaluate(rttMs: 20, jitterMs: 5, lossPct: 0, reconnectCount: 3));
    }

    [Fact]
    public void 重连1到2次_下降一档()
    {
        Assert.Equal(QualityLevel.Good,
            QualityGradeEvaluator.Evaluate(rttMs: 20, jitterMs: 5, lossPct: 0, reconnectCount: 1));
        Assert.Equal(QualityLevel.Good,
            QualityGradeEvaluator.Evaluate(rttMs: 20, jitterMs: 5, lossPct: 0, reconnectCount: 2));
    }
}
