using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Localiza a fronteira do primeiro pacote de uma rajada por observacao direta.
///
/// Decifrando continuamente a partir do corpo do primeiro pacote, o texto mantem-se
/// coerente enquanto se estiver dentro desse pacote. Ao passar para o pacote
/// seguinte, os seus 7 bytes de cabecalho — que nunca foram cifrados — entram na
/// cifra, o estado dessincroniza e tudo a seguir degrada-se em ruido.
///
/// O ponto onde a qualidade cai e' a fronteira. Mede-se a fracao de bytes de
/// enchimento (0x00/0xCC) em janelas sucessivas: dentro do pacote e' alta, depois
/// desaba para o valor de dados aleatorios (~0,8%).
/// </summary>
public static class CipherProbe
{
    public static void Run(string path, int windowSize = 32)
    {
        var segs = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == "S2C" && p[2].Length > 0)
            .Select(p => (Time: double.Parse(p[1], CultureInfo.InvariantCulture),
                          Data: Convert.FromHexString(p[2])))
            .ToList();
        var stream = segs.SelectMany(s => s.Data).ToArray();

        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        // pacote 0x20 em 50, corpo 50+7 .. 124  (tamanho 74, confirmado pelo minimo do handler)
        var first = stream.AsSpan(57, 74 - 7).ToArray();
        cipher.Decrypt(first);
        Console.WriteLine($"pacote 0x0020 @50 len=74, corpo decifrado ({first.Length}B):");
        Console.WriteLine("  " + Ascii(first) + "\n");

        // a partir de 124: decifrar em continuo, sem saltar cabecalhos
        int start = 124;
        var rest = stream.AsSpan(start + 7).ToArray();   // saltar so' o cabecalho do pacote em 124
        cipher.Decrypt(rest);

        Console.WriteLine($"decifra continua a partir de 0x{start + 7:x4} ({rest.Length} bytes)");
        Console.WriteLine("janela  offset   enchimento  ascii");
        for (int i = 0; i + windowSize <= Math.Min(rest.Length, 1200); i += windowSize)
        {
            var win = rest.AsSpan(i, windowSize);
            int filler = 0;
            foreach (var b in win) if (b is 0x00 or 0xCC) filler++;
            double ratio = (double)filler / windowSize;
            string bar = new string('#', (int)(ratio * 20));
            Console.WriteLine($"{i / windowSize,5}  0x{start + 7 + i:x4}   {ratio,5:P0} {bar,-20}  {Ascii(win.ToArray())}");
        }
    }

    private static string Ascii(byte[] d) =>
        new(d.Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray());
}
