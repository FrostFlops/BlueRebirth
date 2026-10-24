using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>TRetMagazineInfo（magazine.UpdateMagazineInfo 推送）：可阅读的期刊 id、服务器时间与版本。</summary>
internal sealed record MagazineInfoMessage(IReadOnlyList<int> MagazineIds, long Time, int Version);

/// <summary>TMagazineSubReward：第 Index 个印章箱（从 1 起）的领取时刻，0 表示未领。</summary>
internal sealed record MagazineSubReward(int Index, long Time);

/// <summary>TMagazineSub：一期已领取的印章箱。</summary>
internal sealed record MagazineSubEntry(int MagazineId, IReadOnlyList<MagazineSubReward> Rewards);

/// <summary>TMagazineHero：「今月のトレンド」第 Index 个位置上的舰娘（HeroId 0 = 空）。</summary>
internal sealed record MagazineHeroSlot(int Index, int HeroId, int MagazineId);

/// <summary>TMagazine（magazine.GetMagazine 推送）：印章箱、趋势舰娘与已解锁的往期。客户端每次整表替换三张表。</summary>
internal sealed record MagazineSnapshot(
    IReadOnlyList<MagazineSubEntry> Subs, IReadOnlyList<MagazineHeroSlot> Heroes, IReadOnlyList<MagazineUnlock> Unlocks);

/// <summary>
/// magazine_pb.lua 对应的编解码器。字段号（全部 int32）：
/// TRetMagazineInfo{MagazineIdList 1 (repeated), Time 2, Version 3}；
/// TMagazine{MagazineSub 1, MagazineHero 2, MagazineUnLock 3}（都是 repeated message）；
/// TMagazineSub{MagazineId 1, MagazineSubReward 2 (repeated)}；TMagazineSubReward{Index 1, Time 2}；
/// TMagazineHero / TMagazineAddHero{Index 1, HeroId 2, MagazineId 3}；TMagazineUnLock{MagazineId 1, Time 2}；
/// TMagazineArg{MagazineId 1, Index 2}；TVote{MagazineId 1, VoteList 2}。
/// <para>
/// repeated 字段缺失时客户端解成空表（datachangemanager.lua），但标量没有默认值：MagazineId / Index 被当作表键
/// （magazinedata.lua SetRewardData），Time 与 0 比较，所以每个标量即使为 0 也写出。repeated int32 不打包，
/// 与其它推送一致。
/// </para>
/// </summary>
internal static class MagazineProtocolCodec
{
    /// <summary>期刊列表推送：客户端 MagazineData:SetData 只保留 MagazineIdList。</summary>
    internal const string InfoMethod = "magazine.UpdateMagazineInfo";

    /// <summary>解锁 / 印章箱 / 趋势舰娘推送：客户端 MagazineData:SetRewardData，随后发 GetMagazineMsg 刷新已打开的杂志页。</summary>
    internal const string SnapshotMethod = "magazine.GetMagazine";

    internal static byte[] EncodeInfo(MagazineInfoMessage info)
    {
        ProtocolPackage output = new();
        foreach (int id in info.MagazineIds)
            output.Write(0x08, Int32(id));
        output.Write(0x10, Time(info.Time));
        output.Write(0x18, Int32(info.Version));
        return output.ToArray();
    }

    internal static byte[] EncodeSnapshot(MagazineSnapshot snapshot)
    {
        ProtocolPackage output = new();
        foreach (MagazineSubEntry sub in snapshot.Subs)
        {
            ProtocolPackage entry = new();
            entry.Write(0x08, Int32(sub.MagazineId));
            foreach (MagazineSubReward reward in sub.Rewards)
                entry.Write(0x12, new ProtocolPackage().Write(0x08, Int32(reward.Index)).Write(0x10, Time(reward.Time)).ToArray());
            output.Write(0x0A, entry.ToArray());
        }
        foreach (MagazineHeroSlot hero in snapshot.Heroes)
            output.Write(0x12, new ProtocolPackage()
                .Write(0x08, Int32(hero.Index)).Write(0x10, Int32(hero.HeroId)).Write(0x18, Int32(hero.MagazineId)).ToArray());
        foreach (MagazineUnlock unlock in snapshot.Unlocks)
            output.Write(0x1A, new ProtocolPackage().Write(0x08, Int32(unlock.MagazineId)).Write(0x10, Time(unlock.Time)).ToArray());
        return output.ToArray();
    }

    internal static MagazineInfoMessage DecodeInfo(ReadOnlySpan<byte> payload)
    {
        var ids = new List<int>();
        long time = 0;
        int version = 0;
        ProtocolDecoder.ProtoReader reader = new(payload);
        while (reader.TryReadField(out int field, out int wire))
            switch (field)
            {
                case 1 when wire == 0: ids.Add(ReadInt32(ref reader)); break;
                case 1 when wire == 2:
                    ProtocolDecoder.ProtoReader packed = new(reader.ReadBytes());
                    while (packed.HasRemaining) ids.Add(ReadInt32(ref packed));
                    break;
                case 2 when wire == 0: time = ReadInt32(ref reader); break;
                case 3 when wire == 0: version = ReadInt32(ref reader); break;
                default: reader.Skip(wire); break;
            }
        return new MagazineInfoMessage(ids, time, version);
    }

    internal static MagazineSnapshot DecodeSnapshot(ReadOnlySpan<byte> payload)
    {
        var subs = new List<MagazineSubEntry>();
        var heroes = new List<MagazineHeroSlot>();
        var unlocks = new List<MagazineUnlock>();
        ProtocolDecoder.ProtoReader reader = new(payload);
        while (reader.TryReadField(out int field, out int wire))
        {
            if (wire != 2)
            {
                reader.Skip(wire);
                continue;
            }
            ReadOnlySpan<byte> bytes = reader.ReadBytes();
            switch (field)
            {
                case 1: subs.Add(DecodeSub(bytes)); break;
                case 2:
                    (int index, int heroId, int magazineId) = DecodeThree(bytes);
                    heroes.Add(new MagazineHeroSlot(index, heroId, magazineId));
                    break;
                case 3:
                    (int unlockId, long unlockTime) = DecodePair(bytes);
                    unlocks.Add(new MagazineUnlock(unlockId, unlockTime));
                    break;
            }
        }
        return new MagazineSnapshot(subs, heroes, unlocks);
    }

    /// <summary>请求里的 MagazineId（字段 1）：TMagazineUnLock、TMagazineArg 与 TVote 都是它。</summary>
    internal static int DecodeMagazineId(ReadOnlySpan<byte> args) => DecodeArg(args).MagazineId;

    /// <summary>TMagazineArg{MagazineId 1, Index 2}（magazine.FetchMagazineReward）；格式错误时返回 (0, 0)。</summary>
    internal static (int MagazineId, int Index) DecodeArg(ReadOnlySpan<byte> args)
    {
        try
        {
            (int magazineId, long index) = DecodePair(args);
            return (magazineId, checked((int)index));
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return (0, 0);
        }
    }

    /// <summary>TMagazineAddHero{Index 1, HeroId 2, MagazineId 3}；格式错误时返回全 0。</summary>
    internal static (int Index, int HeroId, int MagazineId) DecodeAddHero(ReadOnlySpan<byte> args)
    {
        try
        {
            return DecodeThree(args);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return (0, 0, 0);
        }
    }

    private static MagazineSubEntry DecodeSub(ReadOnlySpan<byte> payload)
    {
        int magazineId = 0;
        var rewards = new List<MagazineSubReward>();
        ProtocolDecoder.ProtoReader reader = new(payload);
        while (reader.TryReadField(out int field, out int wire))
            switch (field)
            {
                case 1 when wire == 0: magazineId = ReadInt32(ref reader); break;
                case 2 when wire == 2:
                    (int index, long time) = DecodePair(reader.ReadBytes());
                    rewards.Add(new MagazineSubReward(index, time));
                    break;
                default: reader.Skip(wire); break;
            }
        return new MagazineSubEntry(magazineId, rewards);
    }

    /// <summary>字段 1、2 都是 int32 的消息（TMagazineUnLock、TMagazineSubReward、TMagazineArg）。</summary>
    private static (int First, long Second) DecodePair(ReadOnlySpan<byte> payload)
    {
        int first = 0;
        long second = 0;
        ProtocolDecoder.ProtoReader reader = new(payload);
        while (reader.TryReadField(out int field, out int wire))
            switch (field)
            {
                case 1 when wire == 0: first = ReadInt32(ref reader); break;
                case 2 when wire == 0: second = ReadInt32(ref reader); break;
                default: reader.Skip(wire); break;
            }
        return (first, second);
    }

    /// <summary>字段 1–3 都是 int32 的消息（TMagazineHero、TMagazineAddHero）。</summary>
    private static (int First, int Second, int Third) DecodeThree(ReadOnlySpan<byte> payload)
    {
        int first = 0, second = 0, third = 0;
        ProtocolDecoder.ProtoReader reader = new(payload);
        while (reader.TryReadField(out int field, out int wire))
            switch (field)
            {
                case 1 when wire == 0: first = ReadInt32(ref reader); break;
                case 2 when wire == 0: second = ReadInt32(ref reader); break;
                case 3 when wire == 0: third = ReadInt32(ref reader); break;
                default: reader.Skip(wire); break;
            }
        return (first, second, third);
    }

    /// <summary>int32 按 protobuf 规则编码：负数符号扩展成 64 位。</summary>
    private static ulong Int32(int value) => unchecked((ulong)(long)value);

    /// <summary>时刻字段是 int32：截到 [0, int.MaxValue]（到 2038 年都够用）。</summary>
    private static ulong Time(long value) => (ulong)Math.Clamp(value, 0, int.MaxValue);

    private static int ReadInt32(ref ProtocolDecoder.ProtoReader reader) => unchecked((int)(long)reader.ReadVarint());
}
