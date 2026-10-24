using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>用户档案服务：user.SetUserSecretary / ChangeName / SetMessage / SetPlayerHeadFrame / SetHead。</summary>
internal sealed class UserService(GameServices services)
{
    /// <summary>
    /// user.SetPlayerHeadFrame：TUserSetPlayerHeadFrameArg{headFrameId(1, int32)}。
    /// 只接受已拥有的头像框，即 config_player_head_frame 里的 id（见 <see cref="PlayerHeadFrameLoader"/>）；
    /// 其他 id（含缺省的 0）不落盘并返回 false。客户端忽略应答内容，靠随后的 user.UpdateUserInfo 推送更新 HeadFrame。
    /// 誓约框（id 2）要求当前头像所属舰娘已誓约，这一条由客户端头像框页在发送前检查，服务端不重复判定。
    /// </summary>
    internal async Task<bool> SetPlayerHeadFrameAsync(TRequest request, string profileId, CancellationToken ct)
    {
        int frameId = request.Args is null ? 0 : unchecked((int)ProtocolDecoder.DecodeVarintField(request.Args, 1));
        if (!PlayerHeadFrameLoader.Contains(frameId)) return false;
        using (await services.LockAccountAsync(profileId, ct))
        {
            PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
            if (account.Character.HeadFrame != frameId)
                await services.SaveAccountAsync(
                    account with { Character = account.Character with { HeadFrame = frameId } }, ct);
        }
        return true;
    }

    /// <summary>
    /// user.SetHead：TNewHeadBuyHeadArg{ShipFleetId(1, int32), ProfileID(2, int32)}，只用 ProfileID。
    /// 只接受已解锁的头像（见 <see cref="HeadLogic.CanSet"/>：「头像全部解锁」作弊下是 config_profile 里的任意 id），
    /// 其他 id（含缺省的 0）不落盘并返回 false。客户端靠同一请求的 user.UpdateUserInfo 推送更新 Head。
    /// 与 <see cref="SetPlayerHeadFrameAsync"/> 一样只在账号锁内读改写，不先做时间结算：头像不参与任何按时间推进的结算，
    /// 那条 user.UpdateUserInfo 推送只回显存档里已有的值，不会出现结算了却没推送的变化。
    /// </summary>
    internal async Task<bool> SetHeadAsync(TRequest request, string profileId, CancellationToken ct)
    {
        int head = request.Args is null ? 0 : unchecked((int)ProtocolDecoder.DecodeVarintField(request.Args, 2));
        using (await services.LockAccountAsync(profileId, ct))
        {
            PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
            if (!HeadLogic.CanSet(account, ProfileHeadLoader.Catalog, services.Cheats.Heads, head)) return false;
            if (account.Character.Head != head)
                await services.SaveAccountAsync(
                    account with { Character = account.Character with { Head = head } }, ct);
        }
        return true;
    }

    /// <summary>
    /// 处理用户档案更新（秘书舰/改名/签名）。
    /// 解码对应协议的 arg，更新 PlayerCharacter，落盘，返回空响应。
    /// </summary>
    internal async Task<byte[]> BuildUserProfileUpdateAsync(TRequest request, string profileId, CancellationToken ct,
        string field)
    {
        if (request.Args is null) return [];
        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
        PlayerCharacter c = account.Character;

        if (field == "Secretary")
        {
            // TSetUserSecretaryArg: SecretaryId(1, uint32)
            ulong secId = ProtocolDecoder.DecodeVarintField(request.Args, 1);
            if (secId == 0) return [];
            c = c with { SecretaryId = (uint)secId };
        }
        else if (field == "Name")
        {
            // TUserChangeNameArg: Name(1, string)
            string? name = ProtocolDecoder.DecodeStringField(request.Args, 1);
            if (string.IsNullOrWhiteSpace(name)) return [];
            c = c with { Name = name };
        }
        else if (field == "Message")
        {
            // TSetUserMsgArg: Message(1, string)
            string? msg = ProtocolDecoder.DecodeStringField(request.Args, 1);
            c = c with { Message = msg ?? "" };
        }
        else
        {
            return [];
        }

        account = account with { Character = c };
        await services.SaveAccountAsync(account, ct);
        return [];
    }
}
