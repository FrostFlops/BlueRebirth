/// <summary>
/// 单独成文件的测试（ShopStockTests、SweepTests、OutpostTests）共用的辅助方法：protobuf 字段解析、仓库根目录、客户端目录与断言。
/// Program.cs 的顶层测试有自己的一份同名辅助函数。
/// </summary>
internal static class TestSupport
{
    internal static List<(int Field, int Wire, ulong Value, byte[] Bytes)> Fields(byte[] payload)
    {
        var fields = new List<(int, int, ulong, byte[])>();
        int offset = 0;
        while (offset < payload.Length)
        {
            ulong key = ReadVarint(payload, ref offset);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    fields.Add((field, wire, ReadVarint(payload, ref offset), []));
                    break;
                case 2:
                    int length = checked((int)ReadVarint(payload, ref offset));
                    fields.Add((field, wire, 0, payload.AsSpan(offset, length).ToArray()));
                    offset += length;
                    break;
                default:
                    throw new InvalidDataException($"unexpected wire type {wire} in a protobuf payload");
            }
        }
        return fields;
    }

    internal static ulong ReadVarint(byte[] payload, ref int offset)
    {
        ulong value = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = payload[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return value;
        }
    }

    internal static string FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? current = new(start); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "BlueOath.Local.sln"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Repository root (BlueOath.Local.sln) not found");
    }

    /// <summary>
    /// 测试用的日服客户端目录：环境变量 BLUEOATH_CLIENT_PATH 优先（例如指向未打 2023-02 最终热更的 1.4.0 客户端），
    /// 否则为 &lt;repo&gt;\blueoath\blueoath。所有测试都经由它取客户端，同一进程里的静态配置加载器才不会混用两份配置。
    /// </summary>
    internal static string FindClientPath() =>
        Environment.GetEnvironmentVariable("BLUEOATH_CLIENT_PATH") is { Length: > 0 } clientPath
            ? clientPath
            : Path.Combine(FindRepositoryRoot(), "blueoath", "blueoath");

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
