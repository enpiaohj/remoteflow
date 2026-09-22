using RemoteFlow.Application.Services;
using Xunit;

namespace RemoteFlow.Core.Tests;

/// <summary>
/// CSV 解析的边界情况。导入的是用户手工维护的服务器清单，
/// 备注里出现逗号、引号甚至换行都很常见，解析错误会直接导致连接信息错位。
/// </summary>
public class CsvParserTests
{
    [Fact]
    public void 解析基本的表头与数据行()
    {
        var rows = CsvParser.Parse("Name,Host,Port\nDC01,10.10.1.10,3389");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["Name", "Host", "Port"], rows[0]);
        Assert.Equal(["DC01", "10.10.1.10", "3389"], rows[1]);
    }

    [Fact]
    public void 引号内的逗号不作为分隔符()
    {
        var rows = CsvParser.Parse("Name,Notes\nDC01,\"主域控制器，生产环境\"");

        Assert.Equal("主域控制器，生产环境", rows[1][1]);
        Assert.Equal(2, rows[1].Count);
    }

    [Fact]
    public void 连续两个引号表示一个字面量引号()
    {
        var rows = CsvParser.Parse("Name,Notes\nDC01,\"他说\"\"你好\"\"\"");

        Assert.Equal("他说\"你好\"", rows[1][1]);
    }

    [Fact]
    public void 引号内的换行保留在字段中()
    {
        var rows = CsvParser.Parse("Name,Notes\nDC01,\"第一行\n第二行\"");

        Assert.Equal(2, rows.Count);
        Assert.Equal("第一行\n第二行", rows[1][1]);
    }

    [Fact]
    public void 兼容CRLF换行()
    {
        var rows = CsvParser.Parse("Name,Host\r\nDC01,10.10.1.10\r\nDC02,10.10.1.11");

        Assert.Equal(3, rows.Count);
        Assert.Equal("DC02", rows[2][0]);
        // 行尾不应残留 \r
        Assert.Equal("10.10.1.10", rows[1][1]);
    }

    [Fact]
    public void 文件末尾没有换行时最后一行也会被保留()
    {
        var rows = CsvParser.Parse("Name,Host\nDC01,10.10.1.10");

        Assert.Equal(2, rows.Count);
        Assert.Equal("10.10.1.10", rows[1][1]);
    }

    [Fact]
    public void 去除UTF8_BOM以免首列列名匹配失败()
    {
        var rows = CsvParser.Parse("﻿Name,Host\nDC01,10.10.1.10");

        Assert.Equal("Name", rows[0][0]);
    }

    [Fact]
    public void 空字段保留为空字符串而不是被跳过()
    {
        var rows = CsvParser.Parse("Name,Group,Notes\nDC01,,备注");

        Assert.Equal(3, rows[1].Count);
        Assert.Equal(string.Empty, rows[1][1]);
        Assert.Equal("备注", rows[1][2]);
    }

    [Fact]
    public void 空内容返回空结果()
        => Assert.Empty(CsvParser.Parse(string.Empty));
}
