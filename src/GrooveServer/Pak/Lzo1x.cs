namespace GrooveServer.Pak;

/// <summary>
/// LZO1X — a compressao que o DJMAX usa dentro dos .pak.
///
/// O cliente so' descomprime, por isso o que importa aqui e' produzir um fluxo VALIDO, nao
/// produzir o mesmo fluxo que o empacotador original produziu. Qualquer descompressor LZO1X
/// le' o que sai daqui: o formato nao tem cabecalho nem tabela: e' uma sequencia de
/// instrucoes de "copia N literais" e "copia N bytes de ha' M bytes atras".
///
/// O <see cref="Comprimir"/> e' o algoritmo de referencia (lzo1x_1), com uma simplificacao
/// assumida: em vez de partir a entrada em blocos de 0xBFFE bytes para limitar as distancias,
/// guarda posicoes absolutas no dicionario e RECUSA os pares que fiquem a mais de 0xBFFF
/// bytes. Da' no mesmo e evita o estado que a versao original arrasta entre blocos.
///
/// A funcao de dispersao tambem nao e' a do original. Nao faz diferenca nenhuma na
/// correccao — o par candidato e' sempre confirmado byte a byte antes de ser usado — so' na
/// razao de compressao.
/// </summary>
public static class Lzo1x
{
    private const int M2MaxLen = 8;
    private const int M2MaxOffset = 0x0800;
    private const int M3MaxLen = 33;
    private const int M3MaxOffset = 0x4000;
    private const int M3Marker = 32;
    private const int M4MaxLen = 9;
    private const int M4MaxOffset = 0xBFFF;
    private const int M4Marker = 16;

    /// <summary>
    /// Os ultimos 20 bytes nunca podem ser o inicio de um par. E' a regra do compressor de
    /// referencia e existe porque o descompressor copia em blocos de 8 bytes de cada vez:
    /// sem esta margem, o fim de um par podia sair fora do buffer.
    /// </summary>
    private const int Margem = 20;

    private const int DictBits = 14;
    private const int DictSize = 1 << DictBits;

    // ------------------------------------------------------------------ descompressao

    /// <summary>
    /// Descomprime. <paramref name="tamanhoFinal"/> e' o tamanho que o bloco anuncia; serve
    /// de confirmacao, o fluxo diz por si onde acaba.
    /// </summary>
    /// <param name="consumidos">Quantos bytes da entrada foram lidos ate' a' marca de fim.</param>
    private enum Estado { Arranque, Ciclo, PrimeiraCorrida, Par, FimDoPar, ParSeguinte }

    public static byte[] Descomprimir(ReadOnlySpan<byte> entrada, int tamanhoFinal, out int consumidos)
    {
        var saida = new byte[tamanhoFinal];
        int op = 0, ip = 0, t = 0, origem = 0;
        var estado = Estado.Arranque;

        while (true)
        {
            switch (estado)
            {
                case Estado.Arranque:
                    // Um primeiro byte > 17 e' uma corrida inicial de literais.
                    if (entrada[0] > 17)
                    {
                        t = entrada[ip++] - 17;
                        if (t < 4) { estado = Estado.ParSeguinte; continue; }
                        op = Literais(entrada, saida, ref ip, op, t);
                        estado = Estado.PrimeiraCorrida;
                        continue;
                    }
                    estado = Estado.Ciclo;
                    continue;

                case Estado.Ciclo:
                    t = entrada[ip++];
                    if (t >= 16) { estado = Estado.Par; continue; }
                    if (t == 0)
                    {
                        while (entrada[ip] == 0) { t += 255; ip++; }
                        t += 15 + entrada[ip++];
                    }
                    op = Literais(entrada, saida, ref ip, op, t + 3);
                    estado = Estado.PrimeiraCorrida;
                    continue;

                case Estado.PrimeiraCorrida:
                    t = entrada[ip++];
                    if (t >= 16) { estado = Estado.Par; continue; }
                    // M1: par curto logo a seguir a uma corrida de literais.
                    op = Copiar(saida, op, op - (1 + M2MaxOffset) - (t >> 2) - (entrada[ip++] << 2), 3);
                    estado = Estado.FimDoPar;
                    continue;

                case Estado.Par:
                    if (t >= 64)
                    {
                        origem = op - 1 - ((t >> 2) & 7) - (entrada[ip++] << 3);
                        t = (t >> 5) - 1;
                    }
                    else if (t >= 32)
                    {
                        t &= 31;
                        if (t == 0)
                        {
                            while (entrada[ip] == 0) { t += 255; ip++; }
                            t += 31 + entrada[ip++];
                        }
                        origem = op - 1 - (Le16(entrada, ip) >> 2);
                        ip += 2;
                    }
                    else if (t >= 16)
                    {
                        origem = op - ((t & 8) << 11);
                        t &= 7;
                        if (t == 0)
                        {
                            while (entrada[ip] == 0) { t += 255; ip++; }
                            t += 7 + entrada[ip++];
                        }
                        origem -= Le16(entrada, ip) >> 2;
                        ip += 2;
                        if (origem == op) goto fim;        // marca de fim: 0x11 0x00 0x00
                        origem -= 0x4000;
                    }
                    else
                    {
                        origem = op - 1 - (t >> 2) - (entrada[ip++] << 2);
                        op = Copiar(saida, op, origem, 2);
                        estado = Estado.FimDoPar;
                        continue;
                    }
                    op = Copiar(saida, op, origem, t + 2);
                    estado = Estado.FimDoPar;
                    continue;

                case Estado.FimDoPar:
                    t = entrada[ip - 2] & 3;
                    estado = t == 0 ? Estado.Ciclo : Estado.ParSeguinte;
                    continue;

                case Estado.ParSeguinte:
                    op = Literais(entrada, saida, ref ip, op, t);
                    t = entrada[ip++];
                    estado = Estado.Par;
                    continue;
            }
        }

    fim:
        consumidos = ip;
        if (op != tamanhoFinal)
            throw new InvalidDataException($"lzo: produced {op} bytes, the block announces {tamanhoFinal}");
        return saida;
    }

    private static int Literais(ReadOnlySpan<byte> entrada, byte[] saida, ref int ip, int op, int n)
    {
        if (op + n > saida.Length) throw new InvalidDataException("lzo: literais fora do buffer");
        entrada.Slice(ip, n).CopyTo(saida.AsSpan(op));
        ip += n;
        return op + n;
    }

    private static int Copiar(byte[] saida, int op, int origem, int n)
    {
        if (origem < 0 || op + n > saida.Length) throw new InvalidDataException("lzo: par fora do buffer");
        // Byte a byte de proposito: os pares podem sobrepor-se — e' assim que se representa
        // uma repeticao — e uma copia em bloco daria outro resultado.
        for (int i = 0; i < n; i++) saida[op + i] = saida[origem + i];
        return op + n;
    }

    private static int Le16(ReadOnlySpan<byte> b, int i) => b[i] | (b[i + 1] << 8);

    // ------------------------------------------------------------------ compressao

    public static byte[] Comprimir(ReadOnlySpan<byte> entrada)
    {
        // Pior caso do LZO1X: entrada + entrada/16 + 64 + 3.
        var saida = new byte[entrada.Length + entrada.Length / 16 + 64 + 3];
        int op = 0;
        var dict = new int[DictSize];
        Array.Fill(dict, -1);

        int fim = entrada.Length - Margem;
        int ip = Math.Min(4, entrada.Length);   // as 4 primeiras posicoes nunca abrem um par
        int ii = 0;                             // inicio dos literais por escrever

        while (ip < fim)
        {
            uint dv = (uint)(entrada[ip] | (entrada[ip + 1] << 8) |
                             (entrada[ip + 2] << 16) | (entrada[ip + 3] << 24));
            int idx = (int)((dv * 0x1824429dU) >> (32 - DictBits));
            int cand = dict[idx];
            dict[idx] = ip;

            if (cand < 0 || ip - cand > M4MaxOffset ||
                entrada[cand] != entrada[ip] || entrada[cand + 1] != entrada[ip + 1] ||
                entrada[cand + 2] != entrada[ip + 2] || entrada[cand + 3] != entrada[ip + 3])
            {
                // Sem par: avanca. O passo cresce com a corrida de literais, para nao
                // gastar tempo a procurar dentro de dados incompressiveis.
                ip += 1 + ((ip - ii) >> 5);
                continue;
            }

            EscreverLiterais(entrada, saida, ref op, ii, ip - ii);

            int len = 4;
            while (ip + len < entrada.Length && entrada[cand + len] == entrada[ip + len]) len++;

            int off = ip - cand;
            ip += len;
            ii = ip;
            EscreverPar(saida, ref op, off, len);
        }

        EscreverLiteraisFinais(entrada, saida, ref op, ii);
        saida[op++] = M4Marker | 1;
        saida[op++] = 0;
        saida[op++] = 0;
        return saida.AsSpan(0, op).ToArray();
    }

    /// <summary>Corrida de literais que antecede um par.</summary>
    private static void EscreverLiterais(ReadOnlySpan<byte> src, byte[] dst, ref int op, int ii, int t)
    {
        if (t == 0) return;
        if (t <= 3)
        {
            // Cabem nos dois bits que sobram do par anterior. So' acontece depois de um par
            // ja' escrito — o arranque em ip=4 garante que a primeira corrida tem 4 ou mais.
            dst[op - 2] |= (byte)t;
        }
        else if (t <= 18)
        {
            dst[op++] = (byte)(t - 3);
        }
        else
        {
            dst[op++] = 0;
            int tt = t - 18;
            while (tt > 255) { tt -= 255; dst[op++] = 0; }
            dst[op++] = (byte)tt;
        }
        src.Slice(ii, t).CopyTo(dst.AsSpan(op));
        op += t;
    }

    /// <summary>O que sobra no fim, que ja' nao pode fazer parte de nenhum par.</summary>
    private static void EscreverLiteraisFinais(ReadOnlySpan<byte> src, byte[] dst, ref int op, int ii)
    {
        int t = src.Length - ii;
        if (t == 0) return;
        if (op == 0 && t <= 238)
        {
            // Nada foi escrito ainda: e' a corrida inicial, com codificacao propria.
            dst[op++] = (byte)(17 + t);
        }
        else if (t <= 3)
        {
            dst[op - 2] |= (byte)t;
        }
        else if (t <= 18)
        {
            dst[op++] = (byte)(t - 3);
        }
        else
        {
            dst[op++] = 0;
            int tt = t - 18;
            while (tt > 255) { tt -= 255; dst[op++] = 0; }
            dst[op++] = (byte)tt;
        }
        src.Slice(ii, t).CopyTo(dst.AsSpan(op));
        op += t;
    }

    private static void EscreverPar(byte[] dst, ref int op, int off, int len)
    {
        if (len <= M2MaxLen && off <= M2MaxOffset)
        {
            off -= 1;
            dst[op++] = (byte)(((len - 1) << 5) | ((off & 7) << 2));
            dst[op++] = (byte)(off >> 3);
        }
        else if (off <= M3MaxOffset)
        {
            off -= 1;
            if (len <= M3MaxLen)
            {
                dst[op++] = (byte)(M3Marker | (len - 2));
            }
            else
            {
                int l = len - M3MaxLen;
                dst[op++] = M3Marker;
                while (l > 255) { l -= 255; dst[op++] = 0; }
                dst[op++] = (byte)l;
            }
            dst[op++] = (byte)(off << 2);
            dst[op++] = (byte)(off >> 6);
        }
        else
        {
            off -= 0x4000;
            if (len <= M4MaxLen)
            {
                dst[op++] = (byte)(M4Marker | ((off >> 11) & 8) | (len - 2));
            }
            else
            {
                int l = len - M4MaxLen;
                dst[op++] = (byte)(M4Marker | ((off >> 11) & 8));
                while (l > 255) { l -= 255; dst[op++] = 0; }
                dst[op++] = (byte)l;
            }
            dst[op++] = (byte)(off << 2);
            dst[op++] = (byte)(off >> 6);
        }
    }
}
