using GrooveServer.Crypto;

namespace GrooveServer.Protocol;

/// <summary>
/// O <c>GameInfoInf</c> (0x007A) e' a excecao a' regra do tamanho fixo.
///
/// Todas as outras mensagens tem tamanho fixo por message id. Esta nao: transporta os
/// dados da musica escolhida, que variam de musica para musica. O comprimento vem num
/// campo do proprio corpo, no offset 128 — mas esse campo esta' DENTRO da zona cifrada.
///
/// O cliente resolve-o decifrando primeiro o cabecalho de 132 bytes do corpo, lendo dali
/// o tamanho do bloco e so' depois consumindo o resto. Uma cifra de fluxo permite-o:
/// decifrar em dois pedacos e' equivalente a decifrar de uma vez.
///
/// Layout do corpo:
///   [0..1]     0
///   [2..3]     por identificar; varia de jogada para jogada entre 0x0B e 0x1C, e o cliente
///              nao o le' — ver <see cref="SequenciaOffset"/>. (Dizia-se aqui "0x000E fixo";
///              era so' o valor da primeira amostra.)
///   [4..7]     id da musica (uint32 LE) — o mesmo que o cliente enviou no ChangeDiscReq
///   [8..127]   cabecalho + 7 registos de 16 bytes
///   [128..131] comprimento do bloco (uint32 LE)
///   [132..]    bloco de dados da musica
///
/// Total do pacote = 7 (cabecalho) + 132 + comprimento do bloco.
/// </summary>
public static class GameInfoFraming
{
    public const ushort MessageId = 0x007A;

    /// <summary>Bytes do corpo que e' preciso decifrar para saber o tamanho total.</summary>
    public const int PrefixLength = 132;

    /// <summary>Offset do campo de comprimento, dentro do corpo.</summary>
    public const int LengthOffset = 128;

    /// <summary>
    /// COURSE MODE: o prefixo do bloco diz ao cliente que esta' num course e em que ETAPA.
    ///
    /// Medido comparando o bloco que o servidor real enviou em cada etapa do "Let's Begin"
    /// (course2_s1) com o mesmo chart colhido da biblioteca, que veio de free mode. Os dois
    /// tem o mesmo tamanho e diferem em nove ou dez bytes do prefixo; destes, dois tem
    /// leitura limpa e igual nas tres etapas:
    ///
    ///   offset   etapa 1   etapa 2   etapa 3   biblioteca
    ///     +10       01        01        01         00      <- e' um course
    ///     +14       00        01        02         00      <- indice da etapa, base zero
    ///
    /// Servir o bloco da biblioteca sem estes dois bytes diz ao cliente, em TODAS as etapas,
    /// "course nao, etapa 0". O que se via era: medidor de HP inerte na primeira musica, e no
    /// fim da segunda o ecra de resultados da primeira com a segunda anunciada outra vez —
    /// porque para o cliente nunca se saiu da etapa 0.
    ///
    /// Confirmado pela negativa: servindo a rajada gravada inteira (--course-gravado), onde
    /// estes bytes ja' vem certos, o course corre de ponta a ponta.
    ///
    /// Os restantes bytes que diferem (+2, e um punhado entre +66 e +127) nao tem leitura
    /// estavel entre etapas e ficam por explicar; o +2 e' um contador de sessao ja' conhecido
    /// e sabe-se que nao causa problemas.
    /// </summary>
    public const int CourseFlagOffset = 10;

    /// <summary>Indice da etapa dentro do course, base zero. Ver <see cref="CourseFlagOffset"/>.</summary>
    public const int CourseStageOffset = 14;

    /// <summary>
    /// O COURSE em si, u16.
    ///
    /// Medido em duas gravacoes de courses DIFERENTES. Alinhando o prefixo em u16:
    ///
    ///                                 +10     +12     +14
    ///   course2_s1  "Let's Begin"    01 00   00 00   00/01/02 00   (course 0)
    ///   full_s1     "Step by Step"   01 00   01 00   00/01/02 00   (course 1)
    ///   full_s1     free mode        00 00   00 00   00 00
    ///
    /// O +12 segue o course e o +14 a etapa, nas tres etapas de cada; em free mode os tres
    /// campos vao a zero. Sao seis etapas de dois courses e uma jogada de free mode.
    ///
    /// Isto so' se pode ver com duas gravacoes de courses diferentes: nas duas do
    /// "Let's Begin", que e' o course 0, este campo vale zero tal como na biblioteca, e por
    /// isso nunca aparecia na comparacao byte a byte que deu o +10 e o +14. Servir zero aqui
    /// fazia o cliente anunciar sempre o "Let's Begin" e carregar a capa da musica 126,
    /// fosse qual fosse o course escolhido — tocava a chart certa e mostrava a errada.
    /// </summary>
    public const int CourseIdOffset = 12;

    /// <summary>
    /// O +2 DO CORPO — e ainda nao se sabe o que e'. O que se sabe e' o que NAO e'.
    ///
    /// **NAO E' UM CONTADOR DE JOGADA**, que era o que aqui estava escrito. A ideia veio da
    /// course2_s1, onde as tres etapas dao 0x0B, 0x0C e 0x0D — consecutivos, convincente. Mas
    /// basta olhar para uma sessao inteira de free mode para a desfazer: na end_s1, treze
    /// musicas seguidas dao
    ///
    ///     0E 0F 13 0B 13 0F 12 12 14 0C 0C 13 10
    ///
    /// que nao sobe nem desce. Na course_s1 da' 12, 10, 0C, 1C e na course3_s1 0B, 13, 12.
    /// O trio consecutivo da course2_s1 foi coincidencia.
    ///
    /// **NAO E' DADO DA MUSICA.** A course_s1 e a course2_s1 sao o MESMO course com as MESMAS
    /// tres musicas (126, 123, 55) e dao (0x12, 0x10, 0x0C) numa e (0x0B, 0x0C, 0x0D) na outra.
    ///
    /// **NAO E' A VELOCIDADE.** A sonda que forca este byte a 20 e a 40 nao muda nada no ecra,
    /// logo o cliente nao o le' — ver o interruptor `Velocidade` em Net.ResponsiveSession.
    ///
    /// Fica o que se mediu: valores entre 0x0B e 0x1C, sem ordem, e o cliente indiferente ao
    /// que la' va'. Tambem nao e' o 0x000E fixo que a descricao do corpo aqui em cima dizia —
    /// esse era o valor da primeira amostra que se olhou.
    /// </summary>
    public const int SequenciaOffset = 2;

    public static bool PodeMarcarCourse(byte[] corpo) => corpo.Length > CourseStageOffset;

    /// <summary>Marca o bloco como sendo a etapa <paramref name="etapa"/> de um course.</summary>
    public static void MarcarCourse(byte[] corpo, int course, int etapa, byte? sequencia = null)
    {
        corpo[CourseFlagOffset] = 1;
        BitConverter.TryWriteBytes(corpo.AsSpan(CourseIdOffset, 2), (ushort)Math.Clamp(course, 0, ushort.MaxValue));
        corpo[CourseStageOffset] = (byte)Math.Clamp(etapa, 0, 255);
        if (sequencia is { } s) corpo[SequenciaOffset] = s;
    }

    /// <summary>
    /// Tamanho total do pacote em <paramref name="offset"/>, decifrando o prefixo com
    /// <paramref name="cipher"/>. O prefixo decifrado sai em <paramref name="prefix"/>
    /// para nao ser preciso repetir o trabalho — o estado da cifra ja' avancou.
    /// </summary>
    public static int ReadTotalLength(byte[] stream, int offset, DjMaxCipher cipher, out byte[] prefix)
    {
        int bodyStart = offset + PacketCodec.HeaderSize;
        if (bodyStart + PrefixLength > stream.Length)
            throw new InvalidOperationException("stream too short for the GameInfoInf prefix");

        prefix = stream.AsSpan(bodyStart, PrefixLength).ToArray();
        cipher.Decrypt(prefix);

        int blockLength = BitConverter.ToInt32(prefix, LengthOffset);
        if (blockLength < 0 || blockLength > 8 * 1024 * 1024)
            throw new InvalidOperationException($"comprimento implausivel no GameInfoInf: {blockLength}");

        return PacketCodec.HeaderSize + PrefixLength + blockLength;
    }

    /// <summary>Id da musica que o bloco descreve.</summary>
    public static uint ReadSongId(byte[] body) => BitConverter.ToUInt32(body, 4);

    /// <summary>
    /// Poe no bloco da biblioteca o PREFIXO do bloco que o servidor real mandou nesta etapa,
    /// devolvendo a identidade da musica e o comprimento ao bloco da biblioteca.
    ///
    /// So' se usa quando a etapa toca A MESMA musica que a gravacao — ai' os dois blocos sao
    /// a mesma chart e a copia e' exata. Medido: dos 9776 bytes do bloco da musica 126 apenas
    /// DEZ diferem entre o gravado e o da biblioteca, e todos dentro do prefixo. Os dados da
    /// chart, do +132 em diante, sao identicos.
    ///
    /// Desses dez, o +2 (contador de jogada) e o +10 (marca de course) ja' se sabiam ler. Os
    /// restantes — +70..73, +116..117, +122..123 na etapa 1 — estao numa zona que parece
    /// conteudo codificado e nao campos fixos: as posicoes que diferem mudam de etapa para
    /// etapa. Nao se sabe le-los, mas sabe-se copia-los.
    /// </summary>
    public static void CopiarPrefixoGravado(byte[] destino, byte[] gravado)
    {
        uint musica = ReadSongId(destino);
        var comprimento = destino.AsSpan(LengthOffset, 4).ToArray();

        Array.Copy(gravado, 0, destino, 0, PrefixLength);

        BitConverter.TryWriteBytes(destino.AsSpan(4, 4), musica);
        comprimento.CopyTo(destino.AsSpan(LengthOffset, 4));
    }
}
