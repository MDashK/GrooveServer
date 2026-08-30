namespace GrooveServer.Tools;

/// <summary>
/// Constroi a tabela de tamanhos por msgid a partir de segmentos TCP isolados.
///
/// Racional: quando a atividade e' lenta, cada pacote logico e' enviado sozinho e
/// o TCP entrega-o no seu proprio segmento. Nesses casos o comprimento do segmento
/// E' o comprimento do pacote. Segmentos que chegam em rajada podem conter varios
/// pacotes colados, por isso so' se confia em segmentos separados no tempo dos
/// vizinhos.
///
/// O cabecalho de 7 bytes nunca e' cifrado, portanto o msgid le-se em claro.
/// </summary>
public static class SizeHarvester
{
    /// <summary>Distancia temporal minima aos vizinhos para o segmento ser considerado isolado.</summary>
    private const double IsolationSeconds = 0.050;

    private record Seg(string Dir, double Time, byte[] Data);

    public static void Run(params string[] paths)
    {
        var segs = new List<Seg>();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) { Console.WriteLine($"(sem {path})"); continue; }
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var p = line.Split('\t');
                if (p.Length < 3 || p[2].Length == 0) continue;
                segs.Add(new Seg(p[0],
                    double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.FromHexString(p[2])));
            }
        }
        Console.WriteLine($"{segs.Count} segmentos carregados de {paths.Length} ficheiro(s)\n");

        // Contagem: msgid -> tamanho -> quantas vezes, so' para segmentos isolados
        var byDir = new Dictionary<string, Dictionary<ushort, Dictionary<int, int>>>();

        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Data.Length < 2) continue;

            // vizinhos na MESMA direcao e stream (aproximacao: mesma direcao)
            double prevGap = double.MaxValue, nextGap = double.MaxValue;
            for (int j = i - 1; j >= 0; j--) if (segs[j].Dir == s.Dir) { prevGap = s.Time - segs[j].Time; break; }
            for (int j = i + 1; j < segs.Count; j++) if (segs[j].Dir == s.Dir) { nextGap = segs[j].Time - s.Time; break; }
            bool isolated = prevGap >= IsolationSeconds && nextGap >= IsolationSeconds;
            if (!isolated) continue;

            ushort id = BitConverter.ToUInt16(s.Data, 0);
            if (!byDir.TryGetValue(s.Dir, out var m)) byDir[s.Dir] = m = new();
            if (!m.TryGetValue(id, out var sizes)) m[id] = sizes = new();
            sizes[s.Data.Length] = sizes.GetValueOrDefault(s.Data.Length) + 1;
        }

        foreach (var (dir, m) in byDir.OrderBy(k => k.Key))
        {
            Console.WriteLine($"===== {dir} — tamanhos de segmentos isolados =====");
            Console.WriteLine("msgid           nome                                tamanhos (contagem)");
            foreach (var (id, sizes) in m.OrderBy(k => k.Key))
            {
                string name = MessageNames.TryGetValue(id, out var n) ? n : "";
                bool known = FramingSolver.ServerToClientIds.Contains(id);
                string flag = dir == "S2C" && !known ? "  [!! msgid desconhecido]" : "";
                string sz = string.Join(", ", sizes.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}"));
                Console.WriteLine($"0x{id:x4} ({id,3})  {name,-34}  {sz}{flag}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>Nomes extraidos do dispatcher do cliente (ver docs/protocolo-mensagens.md).</summary>
    public static readonly Dictionary<ushort, string> MessageNames = new()
    {
        [0x03] = "PingTestInf",       [0x07] = "AliveReq",          [0x08] = "DisconnectPeerInf",
        [0x0A] = "ConnectAck",        [0x0B] = "ChannelInfoInf",    [0x0C] = "PeerCountInf",
        [0x10] = "AuthenticateInAck", [0x12] = "AuthInSNDEKeyReq",  [0x14] = "AuthInSNDRPWDReq",
        [0x16] = "KeepAuthenticateInAck", [0x18] = "LogOutAck",     [0x1A] = "LogInAck",
        [0x1E] = "UserInfoResNotFound", [0x1F] = "UserInfoAck",     [0x20] = "UserIDInfoInf",
        [0x22] = "UserIDInfoAck",     [0x24] = "UpdateUserIconInf", [0x25] = "UpdateUserPropertyInf",
        [0x26] = "UpdUserPropLevelInf", [0x27] = "UpdUserPropMoneyInf", [0x28] = "UpdUserPropRecordInf",
        [0x29] = "UpdUserPropMISCInf", [0x2A] = "UpdInvDefaultItemInf", [0x2B] = "UpdInvEventItemInf",
        [0x2C] = "UpdInvShopItemInf", [0x2D] = "UpdInvMountItemInf", [0x2E] = "UpdInvPresentItemInf",
        [0x2F] = "UpdUserAccountClassInf", [0x31] = "UpdUserAccountNickAck", [0x33] = "UpdUserProfileAck",
        [0x37] = "WChatInf",          [0x39] = "ChatInf",           [0x3A] = "RoomInfoUpdateInf",
        [0x3B] = "RoomInfoEraseInf",  [0x3C] = "WaiterInfoUpdateInf", [0x3D] = "WaiterInfoEraseInf",
        [0x40] = "JoinerListStart",   [0x41] = "JoinerListEnt",     [0x42] = "JoinerListEnd",
        [0x43] = "UserInfoInf",       [0x44] = "InventoryInfoInf",  [0x45] = "MessengerInfoInf",
        [0x47] = "JoinRoomAck",       [0x48] = "PostJoinRoomInf",   [0x4D] = "CreateRoomAck",
        [0x50] = "RoomDescInf",       [0x59] = "TeamControlInf",    [0x5B] = "GameTypeInf",
        [0x5E] = "ReadyInf",          [0x60] = "StartInf",          [0x61] = "JoinEventInf",
        [0x63] = "EventInfoInf",      [0x65] = "PlayStartInf",      [0x68] = "PlaySkipInf",
        [0x6B] = "PlayOverInf",       [0x6D] = "PlayStateInf",      [0x70] = "StageResultExInf",
        [0x71] = "CheckDataReq",      [0x74] = "LeaveRoomAck",      [0x77] = "ChangeDiscInf",
        [0x78] = "AwardInfoInf",      [0x7A] = "GameInfoInf",       [0x7C] = "LoadCompleteInf",
        [0x82] = "CourseListInf",     [0x84] = "CourseRankAck",     [0x86] = "ChangeCourseAck",
        [0x88] = "ContinueCourseAck", [0x89] = "PostCourseItemReq", [0x8C] = "AwardItemInf",
        [0x97] = "BigNewsInf",        [0x9D] = "RoomChangeInfoAck", [0xA1] = "QuickInviteAck",
        [0xA2] = "InviteReq",         [0xA7] = "InviteRejectAck",   [0xB3] = "CRItemInf",
        [0xCA] = "StartParameterInf", [0xD2] = "BillingAuthInf",    [0xD3] = "GameStartInf",
        [0xD4] = "UserAlertInf",      [0xE6] = "AlertCreditInf",    [0xFC] = "EnvironmentInf",
        [0xFE] = "SystemInfoAck",     [0x10E] = "CipherCommandInf",
    };
}
