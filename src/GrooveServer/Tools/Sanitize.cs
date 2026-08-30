using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Tira as CREDENCIAIS de uma gravacao, para ela poder ser publicada.
///
/// O <c>AuthenticateInACCReq</c> (0x0011) que o cliente envia leva o utilizador e a password
/// da conta do servidor original, por baixo de uma segunda camada de ofuscacao. Nao esta'
/// revertida — mas o <c>creds</c>, que esta' neste mesmo projecto, tira-as de la'. Publicar
/// uma gravacao com esta mensagem intacta e' publicar a password.
///
/// O servidor NAO precisa do conteudo: das mensagens do cliente le' so' o id e o tamanho, que
/// vao no cabecalho em claro, para saber a que pedido corresponde cada resposta. O corpo nunca
/// e' decifrado. Por isso zera-se o corpo e deixa-se o cabecalho e o comprimento como estavam,
/// e a gravacao continua a servir para tudo.
/// </summary>
public static class Sanitize
{
    /// <summary>O pedido de autenticacao. Ver Protocol/Credentials.cs.</summary>
    private const ushort AuthReq = 0x0011;

    public static void Run(string path, bool escrever)
    {
        var linhas = File.ReadAllLines(path);
        int limpas = 0, jaLimpas = 0;

        for (int i = 0; i < linhas.Length; i++)
        {
            var campos = linhas[i].Split('\t');
            if (campos.Length < 3 || campos[0] != "C2S" || campos[2].Length < 4) continue;

            var bytes = Convert.FromHexString(campos[2]);
            bool mexeu = false;

            // Um segmento TCP pode levar varias mensagens coladas; percorrem-se todas.
            int pos = 0;
            while (pos + 2 <= bytes.Length)
            {
                ushort id = BitConverter.ToUInt16(bytes, pos);
                if (!MessageSizes.ClientToServer.TryGetValue(id, out int tam)) break;
                if (pos + tam > bytes.Length) break;

                if (id == AuthReq && tam > PacketCodec.HeaderSize)
                {
                    // Ja' zerada conta como limpa: a ferramenta corre-se duas vezes sem
                    // querer, e dizer "encontrei credenciais" na segunda seria mentira.
                    bool jaVazia = true;
                    for (int b2 = pos + PacketCodec.HeaderSize; b2 < pos + tam; b2++)
                        if (bytes[b2] != 0) { jaVazia = false; break; }

                    if (jaVazia) jaLimpas++;
                    else
                    {
                        Array.Clear(bytes, pos + PacketCodec.HeaderSize, tam - PacketCodec.HeaderSize);
                        mexeu = true;
                        limpas++;
                    }
                }
                pos += tam;
            }

            if (mexeu)
            {
                campos[2] = Convert.ToHexString(bytes).ToLowerInvariant();
                linhas[i] = string.Join('\t', campos);
            }
        }

        string nome = Path.GetFileName(path);
        if (limpas == 0)
        {
            Console.WriteLine(jaLimpas > 0
                ? $"{nome}: LIMPA ({jaLimpas} mensagem(ns) 0x0011, ja' sem conteudo)"
                : $"{nome}: LIMPA (sem 0x0011)");
            return;
        }

        if (!escrever)
        {
            Console.WriteLine($"{nome}: ATENCAO — {limpas} mensagem(ns) 0x0011 COM CREDENCIAIS " +
                              "(passe --escrever para as apagar)");
            return;
        }

        File.WriteAllLines(path, linhas);
        Console.WriteLine($"{nome}: {limpas} mensagem(ns) 0x0011 zeradas");
    }
}
