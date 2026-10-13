namespace RemoteFlow.Core.Cloud;

/// <summary>
/// 首次加入已有 Vault 时的「重复条目消重」。
/// <para>
/// 两台机器在启用云同步<b>之前</b>各自手工建过同一台服务器 / 同一个分组，
/// 会得到两条<b>不同 Id</b> 的记录；引擎按 Id 认同一条，无法自行判断二者相等。
/// 本接口在「首次同步已把云端全量拉到本地、对账之前」运行：对本地<b>从未同步过</b>的条目，
/// 按自然键在云端匹配，命中即把本地记录改认云端 Id（并改写引用），不再推一条重复的上去。
/// </para>
/// </summary>
public interface ICloudFirstJoinAdopter
{
    /// <summary>执行认领，返回被改认（合并）掉的本地条目数。0 表示没有可合并的重复。</summary>
    Task<int> AdoptAsync(CancellationToken ct = default);
}
