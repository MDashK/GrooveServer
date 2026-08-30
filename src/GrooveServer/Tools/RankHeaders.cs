using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Le' todos os <c>0x0084 CourseRankAck</c> de uma gravacao e poe' lado a lado o CABECALHO
/// (que vai em claro) e o CONTEUDO da tabela.
///
/// Serve para uma pergunta so': o que e' que distingue, no fio, uma tabela com jogadores de
/// uma tabela vazia? O corpo vazio sai a zeros — igual ao que este servidor manda — por isso
/// se o cliente trata os dois casos de maneira diferente, a diferenca esta' no cabecalho.
/// </summary>
public static class RankHeaders
{
    /// <summary>
    /// Onde e' que um valor de 16 bits aparece na gravacao, mensagem a mensagem. Serve para
    /// achar o id de utilizador da conta: ele viaja no <c>0x0084</c> e tem de vir de algum
    /// campo do login.
    /// </summary>
    public static void Procura(string path, ushort valor)
    {
        var (stream, cipher) = Abre(path);
        int pos = 3 + 47;
        var vistos = new SortedDictionary<string, int>();
        while (Proxima(stream, pos, cipher) is { } msg)
        {
            var (id, cab, body, fim) = msg;
            for (int i = 0; i + 2 <= cab.Length; i++)
                if (BitConverter.ToUInt16(cab, i) == valor)
                    vistos[$"0x{id:x4} cabecalho +{i}"] = vistos.GetValueOrDefault($"0x{id:x4} cabecalho +{i}") + 1;
            for (int i = 0; i + 2 <= body.Length; i++)
                if (BitConverter.ToUInt16(body, i) == valor)
                    vistos[$"0x{id:x4} corpo +{i}"] = vistos.GetValueOrDefault($"0x{id:x4} corpo +{i}") + 1;
            pos = fim;
        }
        foreach (var (onde, quantas) in vistos) Console.WriteLine($"  {onde}  x{quantas}");
        Console.WriteLine($"\n{vistos.Count} sitios com {valor}.");
    }

    /// <summary>
    /// Segue o PERFIL ao longo da gravacao: nivel, XP e MAX de cada <c>0x0025</c>, e a lista
    /// de mensagens entre um e o seguinte.
    ///
    /// Serve para achar o que o servidor manda quando o jogador SOBE DE NIVEL — o cliente tem
    /// uma animacao para isso e nao a mostra, o que quer dizer que ha' um aviso que nao estamos
    /// a enviar.
    /// </summary>
    public static void Perfil(string path)
    {
        var (stream, cipher) = Abre(path);
        int pos = 3 + 47;
        int nivelAnt = -1;
        var entre = new List<string>();

        while (Proxima(stream, pos, cipher) is { } msg)
        {
            var (id, _, body, fim) = msg;
            pos = fim;

            if (id != UserProperty.MessageId || body.Length < UserProperty.MaxOffset + 4)
            {
                entre.Add($"0x{id:x2}");
                continue;
            }

            var (nivel, xp, max) = UserProperty.Read(body);
            bool subiu = nivelAnt >= 0 && nivel != nivelAnt;
            Console.WriteLine($"{(subiu ? " *** SUBIU *** " : "  ")}nivel {nivel,2}  xp {xp,5}  max {max,6}" +
                              (entre.Count > 0 ? $"   [antes: {string.Join(" ", entre)}]" : ""));
            nivelAnt = nivel;
            entre.Clear();
        }
        if (entre.Count > 0) Console.WriteLine($"   [depois do ultimo 0x0025: {string.Join(" ", entre)}]");
    }

    /// <summary>
    /// Todas as jogadas de uma gravacao, com o XP QUE O SERVIDOR REAL DEU. Serve para calibrar
    /// a formula do XP contra numeros verdadeiros em vez de palpites.
    ///
    /// O <c>+37</c> do <c>0x0070</c> e' o XP ganho — confirmado no course_s1, onde os quatro
    /// resultados dizem 37, 42, 116 e 38, e as diferencas do <c>0x0025</c> dao 42 e 38 nas duas
    /// que se podem conferir. O 116 e' a ultima etapa de um course, onde o campo passa a ser o
    /// TOTAL do course: 37+42+37 = 116.
    /// </summary>
    public static void Xp(string path)
    {
        var (stream, cipher) = Abre(path);
        int pos = 3 + 47;
        while (Proxima(stream, pos, cipher) is { } msg)
        {
            var (id, _, body, fim) = msg;
            pos = fim;
            if (id != StageResult.ScreenId || body.Length <= StageResult.ScreenTipoFecho) continue;

            Console.WriteLine(
                $"{Path.GetFileNameWithoutExtension(path),-12} " +
                $"xp={BitConverter.ToUInt16(body, 37),4}  " +
                $"prec={BitConverter.ToSingle(body, 31),7:F2}  " +
                $"brk={BitConverter.ToUInt16(body, StageResult.ScreenBreak),3}  " +
                $"combo={BitConverter.ToUInt16(body, StageResult.ScreenComboAgain),4}  " +
                $"notas={BitConverter.ToUInt16(body, StageResult.ScreenEndCombo),4}  " +
                $"base={BitConverter.ToUInt32(body, StageResult.ScreenBaseScore),7}  " +
                $"bonus={BitConverter.ToUInt32(body, StageResult.ScreenBonus),6}  " +
                $"max={BitConverter.ToUInt16(body, StageResult.ScreenMaxGain),4}  " +
                $"+35={body[35],3}  " +
                $"tipo={body[StageResult.ScreenTipoFecho]}");
        }
    }

    /// <summary>Despeja em hex todas as mensagens de um tipo, cabecalho e corpo.</summary>
    public static void Mensagens(string path, ushort qual, int quantas = 4)
    {
        var (stream, cipher) = Abre(path);
        int pos = 3 + 47, n = 0;
        while (Proxima(stream, pos, cipher) is { } msg)
        {
            var (id, cab, body, fim) = msg;
            int len = fim - pos;
            if (qual == 0xFFFF) Console.WriteLine($"  @{pos,7}  0x{id:x4}  {len} B");
            if (id == qual && ++n <= quantas)
            {
                Console.WriteLine($"=== 0x{id:x4} #{n}, {len} B, cabecalho " +
                                  $"{Convert.ToHexString(cab).ToLowerInvariant()}");
                for (int i = 0; i < body.Length; i += 16)
                {
                    var c = body.AsSpan(i, Math.Min(16, body.Length - i)).ToArray();
                    Console.WriteLine($"  +{i,5}  {string.Join(' ', c.Select(b => b.ToString("x2"))),-47}  " +
                        new string(c.Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray()));
                }
            }
            pos += len;
        }
        Console.WriteLine($"\n{n} mensagens 0x{qual:x4}.");
    }

    /// <summary>
    /// Anda uma mensagem para a frente, devolvendo-a em claro. O <c>GameInfoInf</c> tem de
    /// ser tratado a' parte — e' o unico de tamanho variavel gravado no proprio corpo, e sem
    /// isso a caminhada parava logo na primeira musica (era por isso que estas sondas nunca
    /// chegavam ao 0x002A nem ao 0x0089).
    /// </summary>
    private static (ushort Id, byte[] Cab, byte[] Corpo, int Fim)? Proxima(byte[] stream, int pos, DjMaxCipher cipher)
    {
        if (pos + 2 > stream.Length) return null;
        ushort id = BitConverter.ToUInt16(stream, pos);

        if (id == GameInfoFraming.MessageId)
        {
            int total; byte[] prefix;
            try { total = GameInfoFraming.ReadTotalLength(stream, pos, cipher, out prefix); }
            catch { return null; }
            int rest = total - PacketCodec.HeaderSize - GameInfoFraming.PrefixLength;
            if (rest < 0 || pos + total > stream.Length) return null;
            var tail = stream.AsSpan(pos + PacketCodec.HeaderSize + GameInfoFraming.PrefixLength, rest).ToArray();
            cipher.Decrypt(tail);
            var full = new byte[prefix.Length + tail.Length];
            prefix.CopyTo(full, 0);
            tail.CopyTo(full, prefix.Length);
            return (id, stream.AsSpan(pos, PacketCodec.HeaderSize).ToArray(), full, pos + total);
        }

        int? size = MessageSizes.FromServer(id, stream, pos);
        if (size is null || pos + size.Value > stream.Length) return null;
        int len = size.Value;
        byte[] body = Array.Empty<byte>();
        if (len >= PacketCodec.MinEncryptedSize)
        {
            body = stream.AsSpan(pos + 7, len - 7).ToArray();
            cipher.Decrypt(body);
        }
        return (id, stream.AsSpan(pos, Math.Min(7, len)).ToArray(), body, pos + len);
    }

    private static (byte[] Stream, DjMaxCipher Cipher) Abre(string path)
    {
        var stream = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == "S2C" && p[2].Length > 0)
            .SelectMany(p => Convert.FromHexString(p[2]))
            .ToArray();
        var k = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var w = new uint[8];
        for (int i = 0; i < 8; i++) w[i] = BitConverter.ToUInt32(k, i * 4);
        return (stream, new DjMaxCipher(BitConverter.ToUInt16(k, 0x1C), 0u, w));
    }

    public static void Run(string path, int detalhe = -1)
    {
        var stream = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == "S2C" && p[2].Length > 0)
            .SelectMany(p => Convert.FromHexString(p[2]))
            .ToArray();

        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        Console.WriteLine("  #  cabecalho        course  [5..6]  entradas  corpo  primeira");
        int pos = 3 + 47, n = 0;
        while (Proxima(stream, pos, cipher) is { } msg)
        {
            var (id, cab, body, fim) = msg;
            if (id == CourseRank.MessageId)
            {
                int entradas = 0;
                string primeira = "";
                for (int e = 0; (e + 1) * CourseRank.EntrySize <= body.Length; e++)
                {
                    int b = e * CourseRank.EntrySize;
                    uint score = BitConverter.ToUInt32(body, b + CourseRank.ScoreOffset);
                    if (body[b + CourseRank.NameOffset] == 0 && score == 0) continue;
                    entradas++;
                    if (primeira.Length == 0)
                    {
                        int corta = Array.IndexOf(body, (byte)0, b + CourseRank.NameOffset,
                                                CourseRank.NameLength) - (b + CourseRank.NameOffset);
                        if (corta < 0) corta = CourseRank.NameLength;
                        primeira = System.Text.Encoding.ASCII.GetString(
                            body, b + CourseRank.NameOffset, Math.Max(0, corta)) + " " + score;
                    }
                }
                Console.WriteLine($"{++n,3}  {Convert.ToHexString(cab).ToLowerInvariant(),-16} " +
                    $"{BitConverter.ToUInt16(cab, 3),6}  {BitConverter.ToUInt16(cab, 5),6}  " +
                    $"{entradas,8}  {body.Length,5}  {primeira}");

                if (detalhe >= 0 && BitConverter.ToUInt16(cab, 3) == detalhe)
                {
                    for (int i = 0; i < 96; i += 16)
                        Console.WriteLine($"       +{i,4}  " + Convert.ToHexString(body, i, 16).ToLowerInvariant() +
                            "  " + new string(body.Skip(i).Take(16)
                                .Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray()));
                    for (int e = 0; e < 6; e++)
                    {
                        int b = e * CourseRank.EntrySize;
                        if (b + CourseRank.EntrySize > body.Length) break;
                        int corta = Array.IndexOf(body, (byte)0, b + CourseRank.NameOffset, CourseRank.NameLength)
                                  - (b + CourseRank.NameOffset);
                        if (corta < 0) corta = CourseRank.NameLength;
                        Console.WriteLine($"       #{e + 1}  +0={BitConverter.ToUInt16(body, b),6}  " +
                            $"nome={System.Text.Encoding.ASCII.GetString(body, b + CourseRank.NameOffset, Math.Max(0, corta)),-16} " +
                            $"+26={Convert.ToHexString(body, b + 26, 3)}  " +
                            $"score={BitConverter.ToUInt32(body, b + CourseRank.ScoreOffset),9} " +
                            $"combo={BitConverter.ToUInt32(body, b + CourseRank.ComboOffset),6} " +
                            $"+37={BitConverter.ToUInt32(body, b + 37)} +41={BitConverter.ToUInt16(body, b + 41)}");
                    }
                }
            }
            pos = fim;
        }
        Console.WriteLine($"\n{n} tabelas.");
    }
}
