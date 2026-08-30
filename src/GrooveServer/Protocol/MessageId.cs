namespace GrooveServer.Protocol;

/// <summary>
/// Identificadores de mensagem (bytes 0..1 do pacote, little-endian).
///
/// Os valores confirmados vieram da captura de 2026-08-04 cruzada com os nomes
/// de debug preservados no cliente (DJMaxNet::*). Os que ainda nao estao
/// confirmados estao marcados; sao candidatos vindos so' da captura.
/// </summary>
/// <summary>
/// Campos do perfil dentro do <c>UpdateUserPropertyInf</c> (0x0025).
///
/// Confirmados cruzando quatro capturas com o que o cliente mostrava no ecra:
/// nivel base zero, pontos de experiencia e MAX. A percentagem que o jogador ve' e' a
/// experiencia dividida pelo limiar do nivel.
/// </summary>
/// <summary>
/// <c>LogInAck</c> (0x001A, corpo 40 B) — inclui a DATA E HORA do servidor.
///
/// Descoberto comparando o mesmo campo em tres capturas feitas em dias diferentes:
///
///   end_s1     ea 07 08 00 05 00  17 00 1f 00 3b 00   -> 2026-08-05 23:31:59
///   course2_s1 ea 07 08 00 07 00  16 00 0e 00 31 00   -> 2026-08-07 22:14:49
///   full_s1    ea 07 08 00 08 00  03 00 07 00 05 00   -> 2026-08-08 03:07:05
///
/// Sao seis inteiros de 16 bits seguidos, na ordem ano, mes, dia, hora, minuto, segundo.
/// Reenviar a data da gravacao faz o servidor anunciar um dia que ja' passou, o que e'
/// errado por si so' e pode afetar conteudo com janela temporal.
/// </summary>
public static class LogInAck
{
    public const ushort MessageId = 0x001A;
    public const int DateOffset = 10;   // seis u16: ano, mes, dia, hora, minuto, segundo

    /// <summary>
    /// A MESMA data viaja no ConnectAck, que e' a primeira mensagem da sessao e vai em
    /// claro. Offset 15 no pacote de 47 bytes: 7 de cabecalho mais 8 de corpo — o corpo do
    /// ConnectAck nao tem os dois bytes de resultado que o LogInAck tem a' frente.
    ///
    /// Corrigir so' o LogInAck deixava o cliente com duas datas contraditorias: a da
    /// gravacao no ConnectAck e a de hoje no LogInAck.
    ///
    /// ATENCAO: estes bytes estao DENTRO dos 32 de onde a chave de sessao e' derivada
    /// (offset 7..38). Escrever aqui muda a chave — o que so' e' seguro porque a chave e'
    /// derivada DEPOIS da correccao, e o cliente deriva a dele do que recebe. Nao encher
    /// esta zona com entropia: a estrutura tem de se manter.
    /// </summary>
    public const int ConnectAckDateOffset = 15;

    public static bool PodeCorrigirConnectAck(byte[] pacote) =>
        pacote.Length >= ConnectAckDateOffset + 12;

    public static void EscreverNoConnectAck(byte[] pacote, DateTime agora)
    {
        ReadOnlySpan<int> campos = stackalloc int[]
            { agora.Year, agora.Month, agora.Day, agora.Hour, agora.Minute, agora.Second };
        for (int i = 0; i < campos.Length; i++)
            BitConverter.TryWriteBytes(pacote.AsSpan(ConnectAckDateOffset + i * 2, 2), (ushort)campos[i]);
    }

    public static (int Ano, int Mes, int Dia) LerDoConnectAck(byte[] pacote) =>
        (BitConverter.ToUInt16(pacote, ConnectAckDateOffset),
         BitConverter.ToUInt16(pacote, ConnectAckDateOffset + 2),
         BitConverter.ToUInt16(pacote, ConnectAckDateOffset + 4));

    /// <summary>
    /// O indice do jogador no lobby, dentro do CABECALHO do ConnectAck — portanto em claro,
    /// e a PRIMEIRA coisa que o cliente fica a saber sobre si proprio.
    ///
    /// O mesmo numero volta a aparecer no <see cref="WaiterInfo"/> (+67) e no
    /// <see cref="UserRoomInfo"/> (+52). Medido nas quatro gravacoes, bate nas tres
    /// mensagens de cada uma:
    ///
    /// | gravacao | indice |
    /// |---|---|
    /// | end_s1 | 270 |
    /// | course2_s1 | 83 |
    /// | course_s1 | 0 |
    /// | full_s1 | 174 |
    ///
    /// Servir estas mensagens a partir de gravacoes DIFERENTES fazia o cliente ouvir dois
    /// numeros para o mesmo jogador, e congelava ao entrar no course (ver
    /// ResponsiveSession, onde o 0x0051 e' reescrito com este valor).
    /// </summary>
    public const int ConnectAckIndexOffset = 5;

    public static ushort LerIndiceDoConnectAck(byte[] pacote) =>
        pacote.Length >= ConnectAckIndexOffset + 2
            ? BitConverter.ToUInt16(pacote, ConnectAckIndexOffset) : (ushort)0;

    public static bool CanPatch(byte[] body) => body.Length >= DateOffset + 12;

    public static (int Ano, int Mes, int Dia, int Hora, int Min, int Seg) Ler(byte[] body) =>
        (BitConverter.ToUInt16(body, DateOffset),      BitConverter.ToUInt16(body, DateOffset + 2),
         BitConverter.ToUInt16(body, DateOffset + 4),  BitConverter.ToUInt16(body, DateOffset + 6),
         BitConverter.ToUInt16(body, DateOffset + 8),  BitConverter.ToUInt16(body, DateOffset + 10));

    public static void Escrever(byte[] body, DateTime agora)
    {
        ReadOnlySpan<int> campos = stackalloc int[]
            { agora.Year, agora.Month, agora.Day, agora.Hour, agora.Minute, agora.Second };
        for (int i = 0; i < campos.Length; i++)
            BitConverter.TryWriteBytes(body.AsSpan(DateOffset + i * 2, 2), (ushort)campos[i]);
    }
}

/// <summary>
/// <c>ReadyInf</c> (0x005E) — a mensagem que fecha uma musica e prepara a seguinte. Tudo
/// o que interessa esta' no CABECALHO; o corpo e' constante.
///
/// | offset | campo |
/// |---|---|
/// | [4..5] | indice do jogador no lobby, u16 |
/// | [6]    | saldo de MAX, byte baixo |
///
/// Medido em quatro gravacoes. O indice bate com o que o `WaiterInfoUpdateInf` anuncia em
/// cada sessao (83, 174, 270). O MAX bate com o `UpdateUserPropertyInf` da mesma sessao:
/// 42 e 54 na course2; 14835 e 14850 na end_s1, cujos bytes baixos sao 0xF3 e 0x02 — o
/// salto de 243 para 2 e' a passagem por 256, e e' o que confirma que e' so' um byte.
///
/// Reenviar os valores da gravacao da' ao cliente um saldo e um jogador que nao sao os
/// dele, em contradicao com o que o 0x0025 do mesmo fecho acabou de dizer.
/// </summary>
public static class ReadyInf
{
    public const ushort MessageId = 0x005E;
    public const int IndexOffset = 4;   // u16
    public const int MaxLowOffset = 6;  // byte baixo do saldo

    public static bool CanPatch(byte[] cabecalho) => cabecalho.Length > MaxLowOffset;

    public static (ushort Indice, byte MaxBaixo) Ler(byte[] cabecalho) =>
        (BitConverter.ToUInt16(cabecalho, IndexOffset), cabecalho[MaxLowOffset]);

    public static void Escrever(byte[] cabecalho, ushort indice, int max)
    {
        BitConverter.TryWriteBytes(cabecalho.AsSpan(IndexOffset, 2), indice);
        cabecalho[MaxLowOffset] = (byte)(max & 0xFF);
    }
}

/// <summary>
/// <c>StartParameterInf</c> (0x00CA) — parametros da jogada que vai comecar.
///
/// Os bytes 1..2 do corpo sao um u16 com o TOTAL CORRIDO DO COURSE: exatamente o valor que
/// o <c>0x0070</c> da etapa anterior levava em <see cref="StageResult.ScreenEndCombo"/>.
/// Medido nas duas gravacoes do "Let's Begin", quatro transicoes de etapa em duas sessoes
/// independentes:
///
///   course_s1    0x0070 +10 = 122 -> 0x00CA = 7A 00 (122)
///                0x0070 +10 = 270 -> 0x00CA = 0E 01 (270)   [122 + 148, sem breaks]
///   course2_s1   0x0070 +10 = 122 -> 0x00CA = 7A 00 (122)
///                0x0070 +10 = 146 -> 0x00CA = 92 00 (146)   [com 2 breaks, nao acumulou]
///
/// A identidade e' exata nas quatro. Na primeira etapa vale zero, porque nao ha' anterior.
///
/// O valor de course_s1 passa de 255 (270 nao cabe num byte), o que so' por si mostra que o
/// campo tem dois bytes — escrever so' o byte baixo deixa la' o byte alto da gravacao.
///
/// Servir o que veio da biblioteca da' ao cliente um total que nao corresponde ao que ele
/// proprio acabou de reportar no <c>0x006F</c>.
/// </summary>
/// <summary>
/// A VELOCIDADE, que viaja no CABECALHO (portanto em claro) de duas mensagens:
///
/// - <c>0x00C3</c>, do CLIENTE, logo antes de pedir a musica — e' ele que escolhe;
/// - <c>0x00C4</c>, do SERVIDOR, na rajada de arranque — devolve-lhe o mesmo.
///
/// Os dois campos, com o mesmo esquema nas duas mensagens:
///
/// | offset | campo |
/// |---|---|
/// | [3..4] | indice na lista de velocidades, u16, base 0 |
/// | [5..6] | valor interno de scroll, u16 |
///
/// A lista do jogo tem 13 entradas: x1, x1.5, x2, x2.5, x3, x3.5, x4, x4.5, x5,
/// SPEED DOWN, SPEED UP, CHAOS X, SPEED BAT. Medido na captures/lobby_vel.pcapng, onde o
/// jogador entrou seis vezes na MESMA musica com velocidades escolhidas de proposito:
///
///   escolhida   x1   x2   x3   x5   SPEED UP   x1
///   [3..4]       0    2    4    8         10    0
///   [5..6]       7    9   11   15         16    7
///
/// Seis em seis. Para as velocidades numericas o scroll e' <c>7 + indice</c> (x1=7, x1.5=8,
/// ... x5=15); os modos especiais tem valores proprios.
///
/// SERVIR ISTO DA GRAVACAO E' O BUG DA VELOCIDADE: a end_s1 tem indice 2 nas treze musicas
/// (dai' o free mode arrancar sempre a x2) e a course2_s1 tem indice 0 nas duas primeiras
/// etapas (dai' a primeira musica de qualquer course arrancar a x1), fosse qual fosse a
/// escolha do jogador.
///
/// A leitura anterior — "[4] = BREAKs acumulados do course" — estava desviada um byte: os
/// 0, 0, 2 da course2_s1 sao o [3], o indice de velocidade, e batiam com os breaks por
/// coincidencia em duas amostras.
/// </summary>
/// <summary>
/// OS EFFECTORES — as tres caixas ao lado da velocidade, no ecra de escolha da musica.
///
/// **NAO VAO NO CABECALHO, vao no CORPO do <c>0x00C3</c>**, e e' por isso que a velocidade
/// funcionava e eles nao: o cabecalho so' tem os quatro bytes do par indice/scroll, que e' a
/// caixa da ESQUERDA. As outras tres viajam no corpo de 28 bytes, do qual os 20 primeiros sao
/// definicoes e os 8 ultimos um nonce que muda a cada pedido.
///
/// **O SERVIDOR DEVOLVE-OS NO CORPO DO <c>0x00C4</c>**, os mesmos 20 bytes tal e qual. Servir o
/// corpo gravado — que os tem a zero — anulava a escolha do jogador assim que a musica
/// arrancava. E' o mesmo sintoma que a velocidade tinha antes de se lhe mexer, e a mesma causa:
/// devolver o que a gravacao dizia em vez do que o cliente pediu.
///
/// MEDIDO com onze corridas da MESMA musica (1st-sync, EASY, 5K), uma por effector, na ordem em
/// que a lista os mostra. Duas casas, e nada mais mexeu:
///
/// | corrida | effector | byte |
/// |---|---|---|
/// | 1..4 | FADER BLINK, FADER IN, FADER OUT, FOG | **+2** = 1, 2, 3, 4 |
/// | 5..7 | 5K MIRROR, 5K R-SHIFT, 5K RANDOM | **+8** = 1, 2, 3 |
/// | 8..10 | SPEED DOWN, SPEED UP, CHAOS X | corpo a zeros — sao da caixa da velocidade, e o
///   cabecalho levou os indices 9, 10 e 11 |
/// | 11 | SPEED BAT | cabecalho com indice 12, e **+0** = 11 |
///
/// O <c>+0</c> daquela ultima e' a unica leitura que fica por explicar: so' apareceu uma vez.
/// Nao se interpreta nenhum destes numeros — copiam-se os 20 bytes e pronto, que e' o que o
/// servidor original faz e o que dispensa perceber a numeracao toda.
///
/// **O BYTE 20 NAO E' ECOADO.** No corpo do cliente ele ja' pertence ao nonce (vale 0x0B ou
/// 0x05 nas onze corridas); no do servidor vale sempre 0. Copiar 21 bytes em vez de 20 punha
/// la' lixo.
/// </summary>
public static class Effectores
{
    /// <summary>Quantos bytes do corpo sao definicoes. O resto e' nonce.</summary>
    public const int Tamanho = 20;

    /// <summary>Offsets identificados. Ver a tabela em <see cref="Effectores"/>.</summary>
    public const int FadersOffset = 2;
    public const int ArranjoOffset = 8;

    private static readonly string[] Faders = { "OFF", "FADER BLINK", "FADER IN", "FADER OUT", "FOG" };
    private static readonly string[] Arranjo = { "OFF", "5K MIRROR", "5K R-SHIFT", "5K RANDOM" };

    public static bool PodeLer(byte[] corpo) => corpo.Length >= Tamanho;
    public static bool PodeEscrever(byte[] corpo) => corpo.Length >= Tamanho;

    public static byte[] Ler(byte[] corpo) => corpo.Take(Tamanho).ToArray();

    public static void Escrever(byte[] corpo, byte[] efe)
    {
        for (int i = 0; i < Tamanho && i < corpo.Length && i < efe.Length; i++) corpo[i] = efe[i];
    }

    public static bool Iguais(byte[] corpo, byte[] efe)
    {
        for (int i = 0; i < Tamanho && i < corpo.Length && i < efe.Length; i++)
            if (corpo[i] != efe[i]) return false;
        return true;
    }

    /// <summary>Os que estao ligados, por extenso. Vazio quando nao ha' nenhum.</summary>
    public static string Descrever(byte[] efe)
    {
        var ligados = new List<string>();
        static void Ver(List<string> onde, byte[] e, int offset, string[] nomes)
        {
            if (offset < e.Length && e[offset] > 0)
                onde.Add(e[offset] < nomes.Length ? nomes[e[offset]] : $"?{e[offset]}");
        }
        Ver(ligados, efe, FadersOffset, Faders);
        Ver(ligados, efe, ArranjoOffset, Arranjo);
        for (int i = 0; i < Tamanho && i < efe.Length; i++)
            if (efe[i] != 0 && i != FadersOffset && i != ArranjoOffset)
                ligados.Add($"+{i}={efe[i]}");
        return string.Join(", ", ligados);
    }
}

/// <summary>
/// O ECRA DE BOAS-VINDAS de uma conta nova: nickname, idade e sexo.
///
/// **A conta e' criada no SITE, com utilizador e password; o resto define-se no JOGO.** O
/// servidor pede a caixa com o byte <see cref="Credentials.AckPrimeiroLoginOffset"/> do
/// <c>0x0010</c>, e o cliente responde com DUAS mensagens, que o proprio cliente nomeia:
///
///     0x0030 -> 0x0031  UpdUserAccountNickAck   o nickname
///     0x0032 -> 0x0033  UpdUserProfileAck       a idade e o sexo
///
/// MEDIDO na `conta_nova_s0`, 52 segundos depois do login — o tempo de escrever na caixa. Os
/// campos vao no CABECALHO, que viaja em claro:
///
///     0x0030   30 00 | 0a | 41 78 69 61       [3..] = "Axia"
///     0x0032   32 00 | 44 | 00 16 00 01       [4..5] = 22 (idade), [6] = 1 (sexo)
///
/// **ESTAS DUAS MENSAGENS FALTAVAM NA TABELA DE TAMANHOS**, e a consequencia nao era so' nao as
/// tratar: o enquadramento PARAVA nelas. O `timeline` mostrava a sessao truncada e eu conclui
/// que a caixa nao passava pela rede — passava, e era a ferramenta que cegava ali. Ver
/// MessageSizes.
///
/// O nickname e' o nome que o jogo mostra; o utilizador so' serve para entrar. No
/// <c>0x0043</c> sao campos diferentes: o utilizador em +0 e o nickname em +25.
///
/// **UMA SO' AMOSTRA.** A idade lê-se como u16 pequeno-endian em [4..5], que da' 22 e e'
/// coerente com o resto do protocolo; o sexo lê-se do byte [6]. Com uma amostra nao se
/// distingue isto de outras arrumacoes que dessem o mesmo — mas idade e sexo cabem nestes
/// bytes em qualquer delas.
/// </summary>
/// <summary>
/// O <c>0x0039 ChatInf</c> — a barra de texto do jogo. O PRIMEIRO BYTE DO CORPO E' O TIPO, e e'
/// isso que decide onde o texto aparece:
///
///   0x03  boas-vindas do canal, na janela de chat do lobby
///         "-=== 欢迎来到 'LIGHT' ..." (welcome to the channel)
///   0x02  AVISO DO SISTEMA, na faixa vermelha do topo do ecra
///         "[通知]系统已奖励您 ... 道具,请到您的道具箱领取" (o sistema deu-lhe um item,
///         va' busca-lo a' caixa de items)
///
/// Nos so' mandavamos o 0x03, e por isso a conta nova recebia o item de boas-vindas em silencio.
/// O servidor real manda o 0x02 na PROPRIA sessao do login, entre o <c>0x0010</c> e o
/// <c>0x002F</c> — e' a faixa que se ve por cima do ecra de boas-vindas.
///
/// Medido nas gravacoes: a conta_nova_s0 tem o 0x02 nessa posicao; a full_s1, a course_s1 e a
/// ranking tem o 0x03 no lobby. A course_s1 tem os dois, porque uma subida de nivel tambem
/// premeia com item — ver <see cref="StageResult.ScreenSubiuNivel"/>.
/// </summary>
public static class Aviso
{
    public const ushort MessageId = 0x0039;

    /// <summary>Faixa vermelha no topo: o sistema atribuiu alguma coisa.</summary>
    public const byte DoSistema = 0x02;

    /// <summary>Chat do lobby: as boas-vindas do canal.</summary>
    public const byte DoCanal = 0x03;

    public static bool EDoSistema(byte[] corpo) =>
        corpo.Length > 0 && corpo[0] == DoSistema;

    // OS DOIS MOLDES, EM BYTES GBK. Trabalha-se em bytes e nao em texto de proposito: decodificar
    // GBK em .NET obriga a registar o CodePagesEncodingProvider e a trazer um pacote so' para
    // isso, quando tudo o que e' preciso e' reconhecer um prefixo e um sufixo fixos e ficar com
    // o que esta' no meio.
    //
    //   premio: "[通知]系统已奖励您 " + NOME + " 道具，请到您的道具箱领取，祝您游戏愉快。"
    //   canal:  "-== 欢迎来到'" + CANAL + "'频道. ==-"

    private static readonly byte[] PrefixoDoPremio =
        { 0x5B, 0xCD, 0xA8, 0xD6, 0xAA, 0x5D, 0xCF, 0xB5, 0xCD, 0xB3,
          0xD2, 0xD1, 0xBD, 0xB1, 0xC0, 0xF8, 0xC4, 0xFA, 0x20 };

    private static readonly byte[] SufixoDoPremio =
        { 0x20, 0xB5, 0xC0, 0xBE, 0xDF, 0xA3, 0xAC, 0xC7, 0xEB, 0xB5, 0xBD, 0xC4, 0xFA, 0xB5,
          0xC4, 0xB5, 0xC0, 0xBE, 0xDF, 0xCF, 0xE4, 0xC1, 0xEC, 0xC8, 0xA1, 0xA3, 0xAC, 0xD7,
          0xA3, 0xC4, 0xFA, 0xD3, 0xCE, 0xCF, 0xB7, 0xD3, 0xE4, 0xBF, 0xEC, 0xA1, 0xA3 };

    private static readonly byte[] PrefixoDoCanal =
        { 0x2D, 0x3D, 0x3D, 0x20, 0xBB, 0xB6, 0xD3, 0xAD, 0xC0, 0xB4, 0xB5, 0xBD, 0x27 };

    private static readonly byte[] SufixoDoCanal =
        { 0x27, 0xC6, 0xB5, 0xB5, 0xC0, 0x2E, 0x20, 0x3D, 0x3D, 0x2D };

    private static bool Comeca(byte[] c, int i, byte[] q) =>
        i + q.Length <= c.Length && q.Select((b, k) => c[i + k] == b).All(x => x);

    /// <summary>O texto, do +1 ate' ao primeiro zero.</summary>
    private static byte[] Texto(byte[] corpo)
    {
        int fim = Array.IndexOf(corpo, (byte)0, 1);
        if (fim < 0) fim = corpo.Length;
        return corpo[1..fim];
    }

    /// <summary>
    /// A NOTIFICACAO EM INGLES. Devolve o corpo novo, ou null se o texto nao for de um molde
    /// conhecido — nesse caso deixa-se passar o original em vez de estragar o que la' esta'.
    ///
    /// **ESTE TEXTO NAO ESTA' NOS FICHEIROS DO JOGO.** Nao ha' nada no TextStock.ini que lhe
    /// corresponda: e' o servidor que manda a frase feita, e nos limitavamo-nos a repetir os
    /// bytes gravados. Traduzi-la e' portanto reescrever a mensagem, nao editar um .pak.
    ///
    /// O NOME DO ITEM vem em chines dentro da frase; passa pelo <paramref name="nomeEmIngles"/>,
    /// que e' a mesma tabela que traduz os icones. Um nome que ela nao conheca fica como esta' —
    /// a frase fica em ingles com o nome original, que e' melhor do que nao traduzir nada.
    /// </summary>
    public static byte[]? EmIngles(byte[] corpo, Func<byte[], string?> nomeEmIngles)
    {
        if (corpo.Length < 2) return null;
        var texto = Texto(corpo);

        if (Comeca(texto, 0, PrefixoDoPremio))
        {
            int ini = PrefixoDoPremio.Length;
            int fim = texto.Length - SufixoDoPremio.Length;
            if (fim <= ini || !Comeca(texto, fim, SufixoDoPremio)) return null;

            var cru = texto[ini..fim];
            var nome = nomeEmIngles(cru) ?? System.Text.Encoding.Latin1.GetString(cru);
            return Corpo(corpo[0],
                $"[Notice] You received the item {nome}. " +
                "Please collect it from your item box. Have fun!");
        }

        if (Comeca(texto, 0, PrefixoDoCanal))
        {
            int ini = PrefixoDoCanal.Length;
            int fim = texto.Length - SufixoDoCanal.Length;
            if (fim <= ini || !Comeca(texto, fim, SufixoDoCanal)) return null;

            // O nome do canal ja' vem em ASCII ("LIGHT/.[5KEY] Classic").
            var canal = System.Text.Encoding.Latin1.GetString(texto[ini..fim]);
            return Corpo(corpo[0], $"-== Welcome to the '{canal}' channel. ==-");
        }

        return null;
    }

    /// <summary>Um corpo novo: o byte do tipo, o texto, e o zero que o fecha.</summary>
    public static byte[] Corpo(byte tipo, string texto)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(texto);
        var corpo = new byte[1 + t.Length + 1];
        corpo[0] = tipo;
        t.CopyTo(corpo, 1);
        return corpo;
    }
}

public static class BoasVindas
{
    public const ushort NicknameReq = 0x0030;
    public const ushort NicknameAck = 0x0031;
    public const ushort PerfilReq = 0x0032;
    public const ushort PerfilAck = 0x0033;

    /// <summary>O nickname comeca aqui, no cabecalho, e continua pelo corpo.</summary>
    public const int NicknameOffset = 3;

    /// <summary>A idade, u16. Confirmada em quatro contas: 22, 23, 32 e 45.</summary>
    public const int IdadeOffset = 4;

    /// <summary>
    /// O SEXO. **Estava no byte errado.** Tinha-o no +6 por ele valer 1 na unica amostra que
    /// havia — a Axia, feminina. Criadas mais contas, incluindo uma masculina, o +6 deu 1 em
    /// TODAS: e' constante, nao e' o sexo. Tres contas com o mesmo valor e uma delas masculina
    /// chegam para o excluir.
    ///
    /// Fica no +3, que na amostra feminina vale 0 — e 0 e' exactamente o que o painel de perfil
    /// espera para feminino (ver <see cref="UserInfo.SexoOffset"/>). As duas mensagens usarem a
    /// mesma contagem era o mais provavel desde o principio, e eu preferi um byte que "batia"
    /// com uma amostra a um que batia com o resto do protocolo.
    ///
    /// Guarda-se 1-baseado (1 feminino, 2 masculino) para que zero continue a querer dizer
    /// "ainda nao escolheu".
    ///
    /// CONFIRMADO no jogo: varias contas criadas de seguida, masculinas e femininas, sairam
    /// todas certas. Deixou de ser inferencia por eliminacao.
    /// </summary>
    public const int SexoOffset = 3;

    /// <summary>
    /// O nickname: os quatro bytes que cabem no cabecalho em claro, mais o corpo decifrado.
    ///
    /// **O CABECALHO SAO SETE BYTES, E SO' SETE.** A primeira versao apanhava
    /// `pacote[3..10]` — quatro em claro e quatro ainda CIFRADOS — e colava-lhe o corpo
    /// decifrado a seguir, que ja' continha essa mesma regiao. Resultado: `candido5566`
    /// chegava como `cand?3?4ido5566`, com o lixo dos quatro bytes cifrados no meio. Com um
    /// nickname de quatro letras (o `Axia` da captura) o erro nao se via, porque o nome
    /// acabava exactamente onde o cabecalho acaba.
    /// </summary>
    public static string LerNickname(byte[] pacote, byte[] corpo)
    {
        var todo = pacote.Take(PacketCodec.HeaderSize).Skip(NicknameOffset).Concat(corpo).ToArray();
        int fim = Array.IndexOf(todo, (byte)0);
        if (fim < 0) fim = todo.Length;
        return System.Text.Encoding.ASCII.GetString(todo, 0, fim).Trim();
    }

    public static bool PodeLerPerfil(byte[] pacote) => pacote.Length > IdadeOffset + 1;

    public static (int Idade, int Sexo) LerPerfil(byte[] pacote) =>
        (BitConverter.ToUInt16(pacote, IdadeOffset), pacote[SexoOffset] + 1);

    /// <summary>
    /// O pacote em hexadecimal, para o registo. Enquanto o sexo nao estiver visto nas duas
    /// escolhas, vale mais ter os bytes no log do que confiar na leitura.
    /// </summary>
    public static string Cru(byte[] pacote, byte[] corpo) =>
        $"cab {Convert.ToHexString(pacote, 0, Math.Min(PacketCodec.HeaderSize, pacote.Length))}" +
        $" corpo {Convert.ToHexString(corpo, 0, Math.Min(8, corpo.Length))}";
}

public static class Velocidade
{
    public const ushort MessageIdCliente = 0x00C3;
    public const ushort MessageIdServidor = 0x00C4;

    /// <summary>
    /// <c>0x00C5</c> — o cliente mudou de velocidade com o F5 a meio da musica. Mesmo
    /// esquema de cabecalho. Na lobby_vel.pcapng o jogador arrancou a x1 e carregou duas
    /// vezes: sairam dois 0x00C5, com indice 1 (x1.5) e 2 (x2).
    /// </summary>
    public const ushort MessageIdMudanca = 0x00C5;

    public const int IndiceOffset = 3;   // u16
    public const int ScrollOffset = 5;   // u16

    public static bool PodeLer(byte[] cabecalho) => cabecalho.Length >= ScrollOffset + 2;

    public static (ushort Indice, ushort Scroll) Ler(byte[] cabecalho) =>
        (BitConverter.ToUInt16(cabecalho, IndiceOffset),
         BitConverter.ToUInt16(cabecalho, ScrollOffset));

    public static void Escrever(byte[] cabecalho, ushort indice, ushort scroll)
    {
        BitConverter.TryWriteBytes(cabecalho.AsSpan(IndiceOffset, 2), indice);
        BitConverter.TryWriteBytes(cabecalho.AsSpan(ScrollOffset, 2), scroll);
    }

    private static readonly string[] Nomes =
    {
        "x1", "x1.5", "x2", "x2.5", "x3", "x3.5", "x4", "x4.5", "x5",
        "SPEED DOWN", "SPEED UP", "CHAOS X", "SPEED BAT",
    };

    public static string Nome(ushort indice) =>
        indice < Nomes.Length ? Nomes[indice] : $"?{indice}";
}

/// <summary>
/// <c>0x00C9</c> — o registo pessoal da musica que vai comecar.
///
/// EM COURSE MODE O SERVIDOR REAL NAO MANDA REGISTO NENHUM: corpo todo a 0xFF e o
/// cabecalho[6] a 0xFF. Assim nas tres etapas das duas gravacoes de course.
///
/// A biblioteca foi colhida em free mode, onde a mesma mensagem traz mesmo um registo —
/// cabecalho[6] = 0x20 e o corpo a comecar em <c>88 01 00 56 8A 34 01</c>. Servir isso a uma
/// etapa de course anuncia ao cliente um recorde pessoal que nao existe naquele contexto.
/// </summary>
public static class PersonalRecord
{
    public const ushort MessageId = 0x00C9;
    public const int TipoOffset = 6;
    public const byte SemRegisto = 0xFF;

    public static bool CanPatch(byte[] cabecalho) => cabecalho.Length > TipoOffset;

    public static bool JaVazio(byte[] cabecalho, byte[] corpo) =>
        cabecalho[TipoOffset] == SemRegisto && Array.TrueForAll(corpo, b => b == SemRegisto);

    public static void Limpar(byte[] cabecalho, byte[] corpo)
    {
        cabecalho[TipoOffset] = SemRegisto;
        Array.Fill(corpo, SemRegisto);
    }
}

public static class StartParameter
{
    public const ushort MessageId = 0x00CA;

    /// <summary>u16; o mesmo campo que o <see cref="StageResult.ScreenEndCombo"/> da etapa anterior.</summary>
    public const int TotalCorridoOffset = 1;

    public static bool CanPatch(byte[] corpo) => corpo.Length >= TotalCorridoOffset + 2;

    public static int Ler(byte[] corpo) => BitConverter.ToUInt16(corpo, TotalCorridoOffset);

    public static void Escrever(byte[] corpo, int totalCorrido) =>
        BitConverter.TryWriteBytes(corpo.AsSpan(TotalCorridoOffset, 2),
                                   (ushort)Math.Clamp(totalCorrido, 0, ushort.MaxValue));
}

public static class UserProperty
{
    public const ushort MessageId = 0x0025;
    public const int LevelOffset = 2;    // u32, base zero
    public const int XpOffset = 6;       // u32
    public const int MaxOffset = 10;     // u32

    /// <summary>
    /// O MELHOR RESULTADO DE SEMPRE, u32. O mesmo numero viaja em tres mensagens — aqui, no
    /// <see cref="UserInfo.BestScoreOffset"/> (+85) e no
    /// <see cref="UserRoomInfo.BestScoreOffset"/> (+80) — e o painel de perfil le'-o do
    /// 0x0043. Procurando o 252880 da gravacao, aparece nesses tres sitios e em mais nenhum.
    /// </summary>
    public const int RecordeOffset = 26;  // u32

    /// <summary>
    /// O recorde do MODO RANKING — campo DIFERENTE do <see cref="RecordeOffset"/>, e e' este
    /// que a caixa `我的最高得分` do ecra de fim de musica mostra quando se joga em ranking.
    ///
    /// Medido na `gravacoes/ranking.txt`, uma corrida de tres etapas contra o servidor original.
    /// Ao longo das tres, os dois campos andam separados:
    ///
    /// | etapa | +26 (free) | +30 (ranking) |
    /// |---|---|---|
    /// | 1 | 259457 | 678136 |
    /// | 2 | 259457 | 678136 |
    /// | 3 | 259457 | **695467** |
    ///
    /// O +26 nao mexe — e' o recorde de free mode, que aquela corrida nao tocou. O +30 salta na
    /// terceira etapa, que e' onde o total da corrida (695467) bateu o recorde antigo (678136),
    /// e e' esse o numero que o ecra mostrou. **O servidor actualiza-o na propria etapa em que
    /// o recorde cai**, nao no fim.
    ///
    /// **E' SO' DO CANAL 5KEY.** O 7KEY tem campo proprio — ver
    /// <see cref="RecordeRanking7KOffset"/>. Na sessao de 7K este campo leva os 695467 do 5K do
    /// principio ao fim, e o ecra nao os mostra.
    ///
    /// Este servidor ainda nao serve o modo ranking; quando servir, e' este o campo a escrever —
    /// escrever o +26 la' poria o recorde de free mode na caixa errada.
    /// </summary>
    public const int RecordeRankingOffset = 30;  // u32

    /// <summary>
    /// O recorde do modo ranking no canal 7KEY, o irmao do <see cref="RecordeRankingOffset"/>.
    ///
    /// Medido na `gravacoes/ranking7k.txt`, duas corridas seguidas (a primeira acabou em game
    /// over na terceira etapa, a segunda foi ate' ao fim). Os tres campos andam independentes:
    ///
    /// | etapa | +26 free 5K | +30 rank 5K | +46 rank 7K |
    /// |---|---|---|---|
    /// | 1 a 4 | 259457 | 695467 | 0 |
    /// | 5 | 259457 | 695467 | **581128** |
    ///
    /// A quinta e' a ultima da corrida completa, onde o total (581128) virou recorde de 7K. E
    /// bate com os prints do ecra de fim de musica: `我的最高得分` mostrou `0000000` nas duas
    /// primeiras etapas dessa corrida e `0581128` na terceira. **O cliente le' o campo do canal
    /// em que esta'**, e nao um so'.
    /// </summary>
    public const int RecordeRanking7KOffset = 46;  // u32

    /// <summary>Os creditos, u32 — o mesmo numero que o <see cref="UserInfo.CreditosOffset"/>.</summary>
    public const int CreditosOffset = 50;

    /// <summary>
    /// O QUE O PAINEL DE PERFIL MOSTRA EM BAIXO, e que o cliente redesenha a cada fim de
    /// musica a partir DESTA mensagem — nao do <c>0x0043</c> do login.
    ///
    /// Era por isso que uma conta nova, depois de jogar, voltava a mostrar os numeros de quem
    /// gravou a captura: o <c>0x0043</c> ja' ia corrigido, mas o <c>0x0025</c> do fecho da
    /// musica levava o corpo gravado tal e qual. Fechar e reabrir o jogo "corrigia" porque
    /// voltava a valer o <c>0x0043</c>.
    ///
    /// Os tres batem com o painel do MDashK na `end_s1`: combo 453, melhor 99,94%, media 91,24%.
    /// </summary>
    public const int MaxComboOffset = 34;         // u32
    public const int MelhorPrecisaoOffset = 38;   // u32, x100
    public const int PrecisaoMediaOffset = 42;    // u16, x100

    public static bool CanPatch(byte[] body) => body.Length >= MaxOffset + 4;

    /// <summary>
    /// Os tres campos sao de 32 bits, nao de 16.
    ///
    /// Visto em captures/result.pcapng: `2F00 08000000 CF000000 37110000` — nivel 8,
    /// 207 XP, 4407 MAX, todos com dois bytes altos a zero. Enquanto os valores foram
    /// pequenos escrever u16 dava o mesmo resultado, porque os bytes altos da gravacao
    /// tambem eram zero; acima de 65535 truncava. Um jogador pode passar isso — ha' itens
    /// na loja a 168000 MAX.
    /// </summary>
    public static void Write(byte[] body, int level, int xp, int max)
    {
        BitConverter.TryWriteBytes(body.AsSpan(LevelOffset, 4), (uint)level);
        BitConverter.TryWriteBytes(body.AsSpan(XpOffset, 4), (uint)xp);
        BitConverter.TryWriteBytes(body.AsSpan(MaxOffset, 4), (uint)max);
    }

    public static (int Level, int Xp, int Max) Read(byte[] body) =>
        ((int)BitConverter.ToUInt32(body, LevelOffset),
         (int)BitConverter.ToUInt32(body, XpOffset),
         (int)BitConverter.ToUInt32(body, MaxOffset));
}

/// <summary>
/// Modo "music video": o jogador carrega no botao MV e o cliente so' toca o video, sem
/// gameplay.
///
/// Pede-o no byte 9 do corpo do <c>ChangeDiscReq</c> e o servidor tem de o CONFIRMAR no
/// byte 4 do cabecalho do <c>StartInf</c>. Reenviar o StartInf gravado de uma jogada
/// normal faz o cliente entrar em gameplay mesmo tendo pedido MV — a confirmacao e' que
/// manda, nao o pedido.
/// </summary>
public static class MusicVideo
{
    /// <summary>Offset no corpo do ChangeDiscReq onde o cliente pede o modo.</summary>
    public const int RequestOffset = 9;

    /// <summary>Valor observado quando o botao MV foi usado (0x00 no jogo normal).</summary>
    public const byte RequestFlag = 0x14;

    /// <summary>Offset no cabecalho do StartInf onde o servidor confirma.</summary>
    public const int ConfirmOffset = 4;

    public static bool Requested(byte[] changeDiscBody) =>
        changeDiscBody.Length > RequestOffset && changeDiscBody[RequestOffset] == RequestFlag;
}

/// <summary>
/// Campos do perfil dentro do <c>UserInfoInf</c> (0x0043), a mensagem que o servidor
/// envia no login.
///
/// E' esta que enche o ecra de perfil quando o jogador entra. Reescrever so' o
/// <c>UpdateUserPropertyInf</c> (que sai no fim de cada musica) nao chega: ao voltar a
/// entrar, o cliente le' daqui e o progresso parece perdido.
/// </summary>
public static class UserInfo
{
    public const ushort MessageId = 0x0043;

    /// <summary>
    /// O ID DO JOGADOR, no CABECALHO (portanto em claro), em <c>[3..4]</c>.
    ///
    /// Vale 360 em todas as seis gravacoes desta conta. O mesmo numero aparece no
    /// cabecalho do <c>0x003C</c> (+3) e do <c>0x0051</c> (+4), e e' o que a tabela de
    /// courses usa para dizer quem ocupa cada lugar — ver <see cref="CourseRank"/>, onde o
    /// <c>0x0084</c> do course 0 traz 360 no cabecalho com o MDashK em primeiro.
    ///
    /// Nao e' o indice do lobby: esse muda a cada sessao (0, 83, 174, 270) e este nao.
    /// </summary>
    public const int IdOffset = 3;   // u16, no cabecalho

    public static ushort LerId(byte[] cabecalho) =>
        cabecalho.Length >= IdOffset + 2 ? BitConverter.ToUInt16(cabecalho, IdOffset) : (ushort)0;

    /// <summary>
    /// Avatar, u16, os 16 bits baixos do id de catalogo — o mesmo campo que o
    /// <see cref="WaiterInfo"/> tem em +51.
    ///
    /// E' ESTE que pinta o avatar no painel de perfil do lobby. O do WaiterInfoUpdateInf
    /// serve a lista de jogadores, que e' outro elemento do ecra; corrigir so' esse deixava
    /// o perfil com o avatar de quem gravou a captura. Confirmado em cinco capturas, onde
    /// os dois campos levam sempre o mesmo valor.
    /// </summary>
    public const int AvatarOffset = 53;

    public const int LevelOffset = 61;   // u32, base zero
    public const int XpOffset = 65;      // u32
    public const int MaxOffset = 69;     // u32
    public const int BestScoreOffset = 85;

    /// <summary>
    /// O MESMO RECORDE, MAS DO CANAL 7KEY. O painel "我的信息 / SCORE" tem quatro caixas —
    /// `自由模式最高得分` e `排行模式最高得分`, cada um com LIGHT CHANNEL 5KEYS e MANIA CHANNEL
    /// 7KEYS —, portanto o recorde do free mode nunca foi um numero so'. Servia-se o mesmo
    /// valor no 5K e zero no 7K, por nao se saber onde e' que o 7K morava.
    ///
    /// MEDIDO POR ELIMINACAO nas oito gravacoes feitas contra o servidor original: este campo
    /// esta' a ZERO em sete delas e vale <c>0x0003405A = 212570</c> em exactamente uma — a
    /// `del_s1`, que e' a unica tirada DEPOIS de o jogador ter jogado uma musica de 7K de
    /// ponta a ponta. Nas duas da campanha de captura de 7K (`7k_base`, `7k_01`) esta' a zero,
    /// e faz sentido: nessas o jogador entrava no gameplay e saia com ESC sem terminar nada.
    ///
    /// Nenhum outro campo do corpo se comporta assim. E a grandeza bate: 212570 contra os
    /// 259457 do 5K na mesma captura e' o que vale uma musica de 7K facil.
    ///
    /// **CONFIRMADO DEPOIS, e nao ficou em inferencia.** A captura `gravacoes/ranking.txt` foi
    /// tirada com o painel de perfil a mostrar as quatro caixas preenchidas, e os tres numeros
    /// visiveis batem ao certo com tres offsets do corpo:
    ///
    ///     +85   259457   自由模式最高得分 / LIGHT CHANNEL 5KEYS
    ///     +89   678136   排行模式最高得分 / LIGHT CHANNEL 5KEYS
    ///     +117  213082   自由模式最高得分 / MANIA CHANNEL 7KEYS
    ///
    /// O do modo ranking em 7KEY mediu-se depois, com uma corrida de ranking nesse canal
    /// (`gravacoes/ranking7k.txt` e o login que se lhe seguiu): e' o **+105**, e vale 581128.
    /// **NAO era o +121**, que era o que a simetria dos outros tres sugeria — os quatro campos
    /// nao estao aos pares nem por ordem:
    ///
    ///     +85   259457   自由模式 5KEYS
    ///     +89   695467   排行模式 5KEYS
    ///     +105  581128   排行模式 7KEYS
    ///     +117  213082   自由模式 7KEYS
    /// </summary>
    public const int BestScore7KOffset = 117;

    /// <summary>
    /// O recorde do modo RANKING no canal 7KEY, no painel de perfil. Ver
    /// <see cref="BestScore7KOffset"/> para o mapa dos quatro e para o palpite que falhou.
    /// </summary>
    public const int RankingScore7KOffset = 105;

    /// <summary>
    /// O recorde do MODO RANKING no canal 5KEY — o `排行模式最高得分` do painel de perfil.
    ///
    /// Media-se com o resto: na `gravacoes/ranking.txt` vale 678136, que e' exactamente o que o
    /// ecra mostrava. E' a soma das tres etapas de uma corrida de ranking (o print do
    /// TOTAL RESULT dessa corrida deu 695467, o recorde novo).
    ///
    /// Este servidor ainda nao serve o modo ranking; o campo fica identificado para quando o
    /// servir. Ver docs/por-fazer.md D1.
    /// </summary>
    public const int RankingScoreOffset = 89;

    public const int MaxComboOffset = 93;
    public const int BestAccuracyOffset = 97;   // x100
    public const int AvgAccuracyOffset = 101;   // u16, x100

    /// <summary>
    /// O SEXO, no painel de perfil (`性别`). **0 = feminino, 1 = masculino.**
    ///
    /// Medido comparando o 0x0043 da `conta_nova_s1` — a Axia, que escolheu feminino — com o
    /// do MDashK: e' o unico byte que difere fora do nome, da data de nascimento e do avatar.
    ///
    /// **NAO E' A MESMA CONTAGEM DO CLIENTE.** No `0x0032` do ecra de boas-vindas o feminino
    /// vai como 1; aqui vai como 0. Uma e' 1-baseada e a outra 0-baseada, e servir o numero do
    /// cliente tal e qual poe "M" numa conta feminina.
    /// </summary>
    /// <summary>
    /// OS CREDITOS, o <c>CREDIT 0000</c> da caixa do perfil. u32.
    ///
    /// Localizado na highscore_s1 pelo valor: a conta tinha 30 creditos do primeiro dia e nao
    /// os gastou, portanto o campo esta' numa zona que nunca muda — foi preciso procurar o 30
    /// em vez de o ver mexer. Aparece nos dois sitios, aqui no +109 e no
    /// <see cref="UserProperty.CreditosOffset"/> do 0x0025, como todos os campos do painel.
    /// </summary>
    public const int CreditosOffset = 109;

    public const int SexoOffset = 50;

    /// <summary>O ano de nascimento, u16. A Axia, com 22 anos, tem 2004 aqui.</summary>
    public const int AnoNascimentoOffset = 51;

    /// <summary>Avatares por omissao, um por sexo. A Axia nasceu com o 0xF001.</summary>
    public const ushort AvatarFeminino = 0xF001;
    public const ushort AvatarMasculino = 0xF002;

    public static bool CanPatch(byte[] body) => body.Length >= MaxOffset + 4;

    public static void Write(byte[] body, int level, int xp, int max)
    {
        BitConverter.TryWriteBytes(body.AsSpan(LevelOffset, 4), (uint)level);
        BitConverter.TryWriteBytes(body.AsSpan(XpOffset, 4), (uint)xp);
        BitConverter.TryWriteBytes(body.AsSpan(MaxOffset, 4), (uint)max);
    }

    public static (int Level, int Xp, int Max) Read(byte[] body) =>
        ((int)BitConverter.ToUInt32(body, LevelOffset),
         (int)BitConverter.ToUInt32(body, XpOffset),
         (int)BitConverter.ToUInt32(body, MaxOffset));
}

/// <summary>
/// Terceira mensagem que transporta o perfil (0x0051, 128 B), enviada ao entrar na sala.
///
/// Tem esquema proprio, diferente do <see cref="UserInfo"/>. Se nao for reescrita, o
/// cliente recebe o perfil atualizado no login e logo a seguir esta com os valores da
/// gravacao — dados contraditorios sobre o mesmo jogador.
///
/// A experiencia nao esta' localizada: na captura onde foi mapeada valia zero, e um zero
/// nao se distingue dos outros. Fica por identificar.
/// </summary>
public static class UserRoomInfo
{
    public const ushort MessageId = 0x0051;
    public const int LevelOffset = 64;    // u32, base zero
    public const int MaxOffset = 124;     // u32

    /// <summary>
    /// Indice do jogador no lobby, o mesmo do <see cref="LogInAck.ConnectAckIndexOffset"/> e
    /// do <see cref="WaiterInfo.IndexOffset"/>. Medido nas quatro gravacoes: 270, 83, 0, 174,
    /// sempre igual ao do ConnectAck da mesma gravacao.
    /// </summary>
    public const int IndexOffset = 52;    // u16
    public const int BestScoreOffset = 80;
    public const int MaxComboOffset = 88;
    public const int BestAccuracyOffset = 92;
    public const int AvgAccuracyOffset = 96;

    public static bool CanPatch(byte[] body) => body.Length >= MaxOffset + 4;

    public static void Write(byte[] body, int level, int max)
    {
        BitConverter.TryWriteBytes(body.AsSpan(LevelOffset, 4), (uint)level);
        BitConverter.TryWriteBytes(body.AsSpan(MaxOffset, 4), (uint)max);
    }

    public static (int Level, int Max) Read(byte[] body) =>
        ((int)BitConverter.ToUInt32(body, LevelOffset),
         (int)BitConverter.ToUInt32(body, MaxOffset));

    public static bool PodeEscreverIndice(byte[] body) => body.Length >= IndexOffset + 2;

    public static ushort LerIndice(byte[] body) => BitConverter.ToUInt16(body, IndexOffset);

    public static void EscreverIndice(byte[] body, ushort indice) =>
        BitConverter.TryWriteBytes(body.AsSpan(IndexOffset, 2), indice);
}

/// <summary>
/// <c>WaiterInfoUpdateInf</c> (0x003C, corpo de 69 B): a entrada do jogador na lista do
/// lobby. E' ESTA que pinta o avatar no lobby — nao o <c>UpdateUserIconInf</c>.
///
/// Medido comparando os dois 0x003C de captures/equip2.pcapng, um com a mascara vermelha
/// equipada e outro com a Camellia: de 69 bytes, mudaram exatamente dois.
///
/// | offset | campo |
/// |---|---|
/// | +0..23 | nome, ASCII com enchimento a zero |
/// | +25..30 | nome outra vez |
/// | +51..52 | **avatar: 16 bits baixos do id de catalogo** (0x8820 = 100384, 0x8C06 = 101382) |
/// | +67..68 | **indice do jogador no lobby**, muda a cada sessao |
///
/// O indice tem de bater com o <c>+0..1</c> do <c>UpdateUserIconInf</c>: confirmado em tres
/// capturas independentes (0x0149, 0x0158, 0x010E), e na inv_s1 o 0x0024 comeca mesmo por
/// `49 01`. Se os dois nao coincidirem, o cliente aplica a atualizacao do icone a outra
/// entrada da lista e o avatar do jogador nunca muda.
/// </summary>
/// <summary>
/// <c>0x003A RoomInfoUpdateInf</c> (corpo de 44 B) — UMA sala da lista do lobby. O corpo
/// comeca pelo nome da sala; na end_s1 e' o "popskypk1 的自撸房间", que aparecia no lobby
/// como sala fantasma porque a gravacao a trazia.
///
/// Com o lobby vazio o servidor real nao manda nenhuma: medido na vel_s1, gravada de
/// proposito com o lobby limpo — a rajada de login vem sem o 0x3A do fim, e o 0x0073
/// responde "0x74 0x3c" em vez do "0x74 0x3c 0x3a" que da' na eq2_s2.
/// </summary>
public static class RoomInfoUpdate
{
    public const ushort MessageId = 0x003A;
}

/// <summary>
/// <c>0x006F</c> — o relatorio que o cliente manda no fim de uma etapa, mesmo antes do
/// <c>0x006A</c>. O corpo tem em <c>+30</c> a BARRA no fim, em float:
///
/// | gravacao | +30 |
/// |---|---|
/// | course3_s1, as tres etapas passadas | 100.0 |
/// | fail_s1, a etapa que foi abaixo | 0.0 |
///
/// Zero quer dizer etapa falhada, e e' o que decide se vem um <c>0x0087</c> a seguir.
/// </summary>
public static class StageEndReport
{
    public const ushort MessageId = 0x006F;
    public const int BarraOffset = 30;   // float

    public static bool PodeLerBarra(byte[] corpo) => corpo.Length >= BarraOffset + 4;

    public static float LerBarra(byte[] corpo) => BitConverter.ToSingle(corpo, BarraOffset);
}

/// <summary>
/// O DJ MESSENGER, que na gravacao vem com um amigo — o "Evance" — e por isso aparecia
/// sempre na janela. Este servidor nao tem lista de amigos nenhuma, portanto o estado certo
/// e' vazio.
///
/// Duas mensagens, ambas no login. Medidas contra a captures/login_limpo.pcapng, gravada
/// depois de o amigo ter sido mesmo apagado no servidor real:
///
/// | mensagem | com um amigo | sem nenhum |
/// |---|---|---|
/// | <c>0x0020 UserIDInfoInf</c> | 74 B | 7 B (so' cabecalho) |
/// | <c>0x0045</c> cabecalho [3..6] | 1624 (o id do amigo) | 0 |
/// | <c>0x0045</c> corpo +2 | 3 | 0 |
///
/// O <c>0x0020</c> e' de tamanho variavel, com o total no cabecalho — ver
/// <see cref="MessageSizes.LengthInHeader"/>. Vazio fica com 7 bytes, abaixo dos 8 a partir
/// dos quais o corpo e' cifrado, por isso nao ha' corpo nenhum a cifrar e a cifra nao avanca
/// — dos dois lados, que e' o que interessa.
///
/// Era daqui que saia o id que o cliente depois usava no <c>0x0021</c> para perguntar o nome
/// e receber o <c>0x0022</c> com o "Evance".
/// </summary>
public static class Messenger
{
    public const ushort ListaId = 0x0020;
    public const ushort InfoId = 0x0045;

    public const int IdAmigoOffset = 3;    // no CABECALHO das duas
    public const int EstadoOffset = 2;     // no corpo do 0x0045

    /// <summary>O <c>0x0020</c> vazio: cabecalho de 7 bytes que anuncia o proprio tamanho.</summary>
    public static byte[] ListaVazia(byte[] cabecalho)
    {
        var vazio = cabecalho.Take(PacketCodec.HeaderSize).ToArray();
        BitConverter.TryWriteBytes(vazio.AsSpan(IdAmigoOffset, 4), (uint)PacketCodec.HeaderSize);
        return vazio;
    }

    public static bool TemAmigos(byte[] cabecalho) =>
        cabecalho.Length >= IdAmigoOffset + 4 &&
        BitConverter.ToUInt32(cabecalho, IdAmigoOffset) != 0;

    public static void Limpar(byte[] cabecalho, byte[] corpo)
    {
        BitConverter.TryWriteBytes(cabecalho.AsSpan(IdAmigoOffset, 4), 0u);
        if (corpo.Length > EstadoOffset) corpo[EstadoOffset] = 0;
    }
}

public static class WaiterInfo
{
    public const ushort MessageId = 0x003C;
    public const int AvatarOffset = 51;   // u16, 16 bits baixos do catalogo
    public const int IndexOffset = 67;    // u16, indice do jogador no lobby

    /// <summary>
    /// Nivel do jogador na lista do lobby, u32, BASE ZERO — a coluna 等级 do painel
    /// 玩家列表. Medido comparando a end_s1 (8) com a vel_s1 (9): sem o reescrever, o
    /// jogador aparecia como nivel 9 na lista enquanto o painel de perfil dizia 11.
    /// </summary>
    public const int LevelOffset = 59;

    /// <summary>
    /// Avatar por omissao — o boneco azul que aparece quando nao ha' nada equipado.
    ///
    /// Medido em captures/sell.pcapng ao desequipar: o cliente manda `0x0023` com
    /// `02 F0 00 00` no cabecalho e o servidor devolve `0x0024` com `3000 02F0`, onde
    /// 0x0030 e' o indice do jogador. Sem escrever isto, ficar sem avatar deixava passar o
    /// valor da gravacao e o lobby mostrava a mascara vermelha de quem gravou.
    /// </summary>
    public const ushort AvatarPorOmissao = 0xF002;

    public static bool CanPatch(byte[] body) => body.Length >= IndexOffset + 2;

    public static ushort ReadIndex(byte[] body) => BitConverter.ToUInt16(body, IndexOffset);

    /// <summary>O nome comeca no inicio do corpo, ASCII com enchimento a zero.</summary>
    public static string ReadName(byte[] body)
    {
        int n = 0;
        while (n < body.Length && n < 24 && body[n] != 0) n++;
        return System.Text.Encoding.ASCII.GetString(body, 0, n);
    }

    public static int ReadLevel(byte[] body) => (int)BitConverter.ToUInt32(body, LevelOffset);

    public static void WriteLevel(byte[] body, int nivel) =>
        BitConverter.TryWriteBytes(body.AsSpan(LevelOffset, 4), (uint)nivel);

    public static void WriteIndex(byte[] body, ushort indice) =>
        BitConverter.TryWriteBytes(body.AsSpan(IndexOffset, 2), indice);
    public static ushort ReadAvatar(byte[] body) => BitConverter.ToUInt16(body, AvatarOffset);

    public static void WriteAvatar(byte[] body, ushort avatar) =>
        BitConverter.TryWriteBytes(body.AsSpan(AvatarOffset, 2), avatar);
}

/// <summary>
/// <c>UpdateUserIconInf</c> (0x0024, corpo de 18 B): avisa o lobby de que um jogador mudou
/// de avatar. Corpo = `[indice do jogador:u16][avatar:u16][zeros]`.
///
/// O indice e' o mesmo do <see cref="WaiterInfo"/>; o avatar sao os 16 bits baixos do
/// catalogo, e o proprio pedido do cliente (0x0023) ja' os traz no cabecalho, bytes 3..4.
/// </summary>
public static class UserIcon
{
    public const ushort MessageId = 0x0024;
    public const int IndexOffset = 0;
    public const int AvatarOffset = 2;

    public static bool CanPatch(byte[] body) => body.Length >= AvatarOffset + 2;

    public static void Write(byte[] body, ushort indice, ushort avatar)
    {
        BitConverter.TryWriteBytes(body.AsSpan(IndexOffset, 2), indice);
        BitConverter.TryWriteBytes(body.AsSpan(AvatarOffset, 2), avatar);
    }
}

/// <summary>
/// ATENCAO — HA' DOIS "MAX" NO JOGO, e aparecem lado a lado no mesmo ecra:
///
///   MAX (notas)  contagem de notas nao falhadas. `total de notas = MAX + BREAK`.
///                E' o que esta' em <c>StageResult.ScreenMax</c>.
///   MAX (moeda)  a moeda do jogo, com que se compra na loja.
///                E' o que esta' em <c>UserInfo.MaxOffset</c>, <c>PlayerProfile.Max</c> e
///                <c>StageResult.ScreenMaxGain</c>.
///
/// No ecra de resultados do FREE MODE a coluna MAX e' a contagem de notas. No ecra de fim
/// de musica do COURSE MODE a mesma coluna e' a MOEDA — viu-se numa musica com "MAX 16" e
/// "COMBO 148", impossivel se fossem notas, e a conta do saldo fecha ao cent: 239 - 100 do
/// course + 69 ganhos = 208, o valor que a loja mostrava a seguir.
///
/// Nao presumir qual dos dois e' pelo nome da coluna: confirmar pelo ecra.
/// </summary>
/// <summary>
/// Ecra de resultados. Todos os offsets confirmados contra seis fotografias do ecra, em
/// captures/result.pcapng — tres jogadas boas, tres com muitos breaks.
///
/// <c>StageResultInf</c> (0x006F, C2S, corpo 52 B) e' o que o CLIENTE reporta:
/// | offset | campo |
/// |---|---|
/// | +2 | u16, total de notas (MAX + BREAK) |
/// | +36 | u32, combo no fim da musica |
/// | +40 | u32, combo maximo |
/// | +44 | u16, MAX ganho |
///
/// <c>StageResultExInf</c> (0x0070, S2C, corpo 44 B) e' o que o ecra mostra:
/// | offset | campo | Relation / Jupiter / Enemy / ON / Save / Seasons |
/// |---|---|---|
/// | +4 | u16 MAX | 276 / 132 / 427 / 517 / 306 / 623 |
/// | +6 | u16 BREAK | 1 / 0 / 3 / 29 / 23 / 38 |
/// | +8, +14 | u16 COMBO maximo | 264 / 132 / 311 / 128 / 101 / 91 |
/// | +10 | u16 combo no fim | |
/// | +18 | float, 100.0 em todas |
/// | +23 | u32 pontuacao base | 220541 / 211573 / 224506 / 178860 / 175295 / 184309 |
/// | +27 | u32 bonus | 5000 / 30000 / 5000 / 0 / 0 / 0 |
/// | +31 | float precisao | 98.31 / 99.85 / 97.19 / 80.54 / 81.42 / 83.45 |
/// | +35 | u16 disco | 9 = GOLDEN DISC, 6 = GOLDEN MAX, 13 = "A" |
/// | +37 | u16 XP ganho | 47 / 33 / 31 / 29 / 32 |
/// | +39 | u16 MAX ganho | 14 / 20 / 26 / 26 / 39 / 17 |
///
/// O XP foi confirmado contra os saltos da barra de progresso (5,60%, 3,93%, 3,69%,
/// 3,45%, 3,81% x 8,4 pontos por 1%).
///
/// POR RESOLVER: o cliente NAO envia MAX, BREAK, pontuacao, bonus nem precisao — o
/// servidor real calcula-os. O <c>0x0072</c> nao ajuda (e' a resposta ao CheckDataReq,
/// anti-batota) e o <c>0x006C</c> so' traz posicao e HP de 5 em 5 segundos. Ficam os
/// valores da gravacao ate' se perceber de onde saem.
/// </summary>
/// <summary>
/// <c>PlayStateInf</c> (0x006C, C2S, corpo 23 B) — o cliente relata o estado durante a
/// musica. Sao batimentos de 5 em 5 segundos MAIS mensagens por evento.
///
/// | offset | campo |
/// |---|---|
/// | +0..1 | posicao no tema (cresce) |
/// | +2..3 | constante por musica |
/// | +4..7 | **float HP** |
/// | +8..11 | float que cresce ao longo da musica |
/// | +12..13 | 0x9797 na mensagem do break, 0x9796 no seguimento |
/// | +20..21 | 0x2710 nas periodicas, 0xCAB0 nas de evento |
///
/// **Cada break custa exatamente 6.00 de HP** e a recuperacao e' de +0.30 por tique. Como
/// varios breaks seguidos se fundem numa so' queda, conta-se pela GRANDEZA da queda e nao
/// pelo numero de quedas:
///
///     breaks += max(1, arredondar(queda / 6))
///
/// Verificado em NOVE musicas — seis normais e tres com breaks deliberados, incluindo um
/// grupo de 2 e um de 4 feitos de proposito para forcar a fusao. Exemplo da Ray of
/// Illuminati: 118->112 = 6.00 (1), 112->106.30 = 5.70 (1, com um tique de recuperacao
/// pelo meio), 112->94.30 = 17.70 (3).
///
/// O HP INICIAL NAO SE ASSUME: depende dos itens equipados (118 sem nada, 128 com bonus).
/// Assumir 128 criava uma queda fantasma de 10 pontos e contava dois breaks a mais.
/// </summary>
public static class PlayState
{
    public const ushort MessageId = 0x006C;
    public const int HpOffset = 4;
    public const float HpPorBreak = 6.0f;

    /// <summary>
    /// Precisao acumulada, float, a' escala de 1200: <c>precisao = valor / 1200</c>.
    ///
    /// Verificado em nove jogadas contra o que o ecra mostrou — oito batem ao milesimo
    /// (99.8486 vs 99.85, 98.3068 vs 98.31, 80.5422 vs 80.55). E' CUMULATIVA sobre o total
    /// de notas do chart, por isso cresce ao longo da musica; a nona (ForSeasons) ficou em
    /// 81.71 contra 83.45 porque o ultimo batimento apanhou-a longe do fim e ainda faltavam
    /// notas por tocar.
    ///
    /// Os julgamentos por nota sao 100%, 90%, 80% ... 1%, BREAK e FAULT, e a precisao e' a
    /// media deles. Confere: 131 notas a 100% e uma a 80% dao 99.85%, o valor exato que a
    /// Jupiter Driving mostrou com 132 MAX.
    /// </summary>
    public const int AccuracyOffset = 8;
    public const float AccuracyScale = 1200f;

    public static bool CanReadAccuracy(byte[] body) => body.Length >= AccuracyOffset + 4;
    public static float ReadAccuracy(byte[] body) =>
        BitConverter.ToSingle(body, AccuracyOffset) / AccuracyScale;

    public static bool CanReadHp(byte[] body) => body.Length >= HpOffset + 4;
    public static float ReadHp(byte[] body) => BitConverter.ToSingle(body, HpOffset);

    /// <summary>Quantos breaks explica uma queda de HP.</summary>
    /// <summary>
    /// Quantos BREAKs explicam uma queda de HP — zero se a queda for pequena de mais.
    ///
    /// O medidor oscila sozinho. Numa etapa de course medida linha a linha ele faz
    /// 120,00 -> 119,90 -> 120,00 -> 119,90, sempre a recuperar: sao decimas, nao breaks.
    /// Um BREAK custa 6,00 (JUDGMENT_BREAK do Testsong.ini), sessenta vezes mais.
    ///
    /// Havia aqui um <c>Math.Max(1, ...)</c> que dava um break a QUALQUER descida. Uma
    /// musica sem uma unica falha contava quatro, e esse numero ia parar ao MAX do ecra de
    /// resultados, ao XP e ao estado do course.
    ///
    /// Exige-se meio break (3,00) para contar o primeiro; acima disso arredonda-se, porque
    /// varias falhas seguidas fundem-se numa so' queda entre dois relatorios.
    /// </summary>
    public static int BreaksNaQueda(float queda) =>
        queda < HpPorBreak / 2 ? 0
            : Math.Max(1, (int)Math.Round(queda / HpPorBreak, MidpointRounding.AwayFromZero));
}

/// <summary>
/// Pontuacao e disco do ecra de resultados.
///
/// **MAXIMO DO CHART — lei exata.** `maximo = 200000 + 90 x total de notas`, e numa jogada
/// SEM BREAKS `base = maximo x precisao`. Verificado em quatro jogadas all-combo
/// independentes, com erro maximo de 0,05%:
///
///   Save My Dream EZ  128 notas  previsto 211520  real 211520
///   Luv Flow          118 notas  previsto 210094  real 210106
///   Jupiter Driving   132 notas  previsto 211562  real 211573
///   Save My Dream NM  240 notas  previsto 220226  real 220320
///
/// O numero de notas e' o que manda, nao a dificuldade — as dificuldades altas so' pontuam
/// mais porque tem mais notas. A mesma musica em EZ/NM/HD confirma-o.
///
/// **COM BREAKS a lei nao chega:** a pontuacao fica abaixo de `maximo x precisao` numa
/// proporcao que nao e' funcao nem do numero de breaks nem do combo maximo (a Let's Go Baby
/// com 6 breaks todos juntos no inicio perdeu 0,03%, com 3 breaks espalhados perdeu 2,3%).
/// Depende da estrutura dos combos ao longo da musica, que nao esta' decifrada. Nesses
/// casos usa-se um ajuste linear por minimos quadrados sobre 16 jogadas — erro tipico
/// 0,6%, maximo 3,5%. E' aproximacao, nao lei.
/// </summary>
public static class ScoreFormula
{
    public const int BasePorChart = 200000;
    public const int BasePorNota = 90;

    /// <summary>Pontuacao maxima deste chart, so' funcao do numero de notas.</summary>
    public static int MaximoDoChart(int totalNotas) => BasePorChart + BasePorNota * totalNotas;

    /// <summary>
    /// Pontuacao base. Exata quando nao houve breaks; aproximada quando houve.
    ///
    /// **NUMA MUSICA FALHADA O VALOR E' DISPARATADO, E NAO FAZ MAL.** O ajuste linear so' vale
    /// dentro da gama onde foi feito — jogadas ACABADAS, precisao de 90 a 100%, combos de
    /// centenas. Num game over cedo a precisao cai para ~0,05% e o combo para 2 ou 3, e o que
    /// sobra da recta e' quase so' a constante 9950. Contra as cinco musicas perdidas da
    /// `end_s1`, onde o servidor original deu 227, 1029, 346, 1709 e 475, esta formula da'
    /// 10202, 11197, 10328, 11982 e 10506 — errado por 45 vezes.
    ///
    /// Fica assim de proposito, e a razao e' esta: **o cliente NUNCA MOSTRA este numero quando
    /// se perde.** Em free mode aparece "GAME OVER" e mais nada — sem resultados, sem
    /// estatisticas. Em course mode vai-se direto ao ecra de continuar, com o preco em MAX e um
    /// sim/nao (e so' "nao" se o MAX nao chegar), tambem sem dados nenhuns da musica. O campo
    /// viaja e ninguem o le'.
    ///
    /// O unico sitio por onde o valor escapava era o RECORDE PESSOAL do free mode, que nao
    /// tinha guarda de etapa falhada — numa conta nova o primeiro game over ficava la' como
    /// recorde por ser maior que zero. Isso foi tapado em Net.ResponsiveSession, que e' onde
    /// devia estar: o problema nao era a formula, era faltar a guarda.
    ///
    /// Se um dia isto passar a ser visto, ha' uma pista boa: nas cinco amostras o valor real
    /// e' <c>90 x combo maximo</c> a menos de 76 pontos (180/227, 1080/1029, 270/346,
    /// 1710/1709, 450/475). Os 90 sao os mesmos <see cref="BasePorNota"/> da lei exacta, e os
    /// 200000 desta parecem ser um piso de conclusao que uma musica perdida nao recebe.
    /// </summary>
    public static int Base(int totalNotas, float precisao, int comboMaximo, int breaks)
    {
        if (breaks == 0) return (int)Math.Round(MaximoDoChart(totalNotas) * precisao / 100.0);
        return (int)Math.Round(1913.1 * precisao + 89.9 * comboMaximo + 9950);
    }

    /// <summary>
    /// Disco atribuido e o bonus que vale.
    ///
    /// **AFERIDO CONTRA 17 ETAPAS REAIS** — todos os <c>0x0070</c> por etapa das gravacoes
    /// course_s1, course2_s1, course3_s1 e end_s1, onde o codigo esta' em +35 e o bonus em +27.
    /// A tabela anterior falhava em dois pontos, e os dois davam-se a ver nestes numeros:
    ///
    /// | precisao | breaks | disco real | bonus real | o que a tabela antiga dava |
    /// |---|---|---|---|---|
    /// | 100,00 | 0 | 1 STEEL MAX | 40000 | certo |
    /// | 99,93 | 0 | 6 GOLDEN MAX | 30000 | certo |
    /// | 99,58 | 0 | **8 BRONZE MAX** | 20000 | 7 SILVER MAX, 25000 |
    /// | 99,44 / 99,00 / 98,79 | 0 | 8 BRONZE MAX | 20000 | certo |
    /// | 98,31 / 97,08 / 96,76 / 96,16 | 0 | 9 GOLDEN DISC | **15000** | 5000 (e 96,16 dava 10) |
    /// | 97,57 / 97,40 | 2 e 1 | 9 GOLDEN DISC | 5000 | certo |
    /// | ~0,05 | muitos | 0 | 0 | certo |
    ///
    /// **1. O SILVER MAX comeca acima de 99,58**, e nao em 99,50: uma jogada de 99,58 sem
    /// breaks levou BRONZE. O 99,75 que ja' estava anotado como observacao de um SILVER passa
    /// portanto a ser o limiar, e o GOLDEN sobe de 99,80 para o 99,85 tambem ja' observado.
    ///
    /// **2. ABAIXO DA FAMILIA MAX, O ALL COMBO VALE MAIS 10000.** Quatro GOLDEN DISC sem um
    /// unico break deram 15000 e dois com breaks deram 5000 — mesmo disco, bonus diferente. E'
    /// a unica regra que concilia os seis, e nao toca na familia MAX, que ja' exige combo
    /// perfeito e por isso ja' o traz embutido no seu valor.
    ///
    /// **O QUE FICA POR FECHAR:** o limiar do GOLDEN DISC so' se sabe que e' <= 96,16 (o valor
    /// mais baixo que se viu levar um); poe-se em 96,0, que e' a mudanca minima que serve os
    /// dados.
    ///
    /// **OS ESCALOES DE BAIXO, medidos na highscore_s1** — antes nunca tinham aparecido numa
    /// gravacao, e a tabela pagava-lhes a mais:
    ///
    /// | precisao | breaks | codigo real | bonus real | o que a tabela dava |
    /// |---|---|---|---|---|
    /// | 93,20 | 13 | 10 SILVER DISC | 3000 | certo |
    /// | 90,82 | 2 | **11 BRONZE DISC** | **1000** | 10 SILVER DISC, 3000 |
    /// | 88,13 | 11 | 12 (letra) | 0 | 10 SILVER DISC, 3000 |
    /// | 82,60 | 1 | 13 (letra) | 0 | certo |
    /// | 75,11 | 0 | **14** (letra) | 0 + all combo | codigo 13 |
    ///
    /// Existe um BRONZE DISC, que valia 1000 e nao estava na tabela — 90,82 levava SILVER e
    /// tres vezes o bonus. E os breaks nao entram: 93,20 com TREZE breaks levou SILVER e 90,82
    /// com dois levou BRONZE, portanto abaixo da familia MAX o disco sai so' da precisao.
    ///
    /// Os limiares ficam entre as amostras, nao em cima delas: o SILVER esta' algures em
    /// (90,82 ; 93,20] e poe-se em 93,0. Os dos escaloes de letra so' mudam o CODIGO, que nao
    /// paga nada, por isso errar neles nao mexe em pontuacao nenhuma.
    ///
    /// O +10000 do all combo abaixo da familia MAX esta' agora medido: a jogada de 75,11% sem
    /// breaks levou 10000 com um escalao de letra que nao paga disco.
    /// </summary>
    public const int BonusAllCombo = 10000;

    /// <summary>
    /// O BONUS DOS EFFECTORES, que soma ao <c>奖励得分</c> por cima do disco e do all combo.
    ///
    /// **E' UMA TABELA, NAO UMA FORMULA.** A regra que aqui estava — <c>1000 x codigo</c> —
    /// vinha de duas amostras, uma de cada familia (FOG=4 valia 4000, 5K RANDOM=3 valia 3000),
    /// e as duas cabiam nela por acaso. A highscore_s1 mediu as restantes e derrubou-a logo na
    /// primeira: o FADER BLINK e' o codigo 1 e vale 4000, nao 1000.
    ///
    /// As duas familias estao agora COMPLETAS, cada valor visto no ecra:
    ///
    /// | +2 faders |  | +8 arranjo |  |
    /// |---|---|---|---|
    /// | 1 FADER BLINK | 4000 | 1 5K MIRROR | 2000 |
    /// | 2 FADER IN | 3000 | 2 5K R-SHIFT | 2000 |
    /// | 3 FADER OUT | 3000 | 3 5K RANDOM | 3000 |
    /// | 4 FOG | 4000 | | |
    ///
    /// E o SPEED DOWN, sozinho, vale 3000 — a velocidade tambem conta, o que nao se sabia.
    ///
    /// **SOMAM-SE**: medido antes com FOG e 5K RANDOM juntos, que deram 7000 = 4000 + 3000.
    ///
    /// COMO SE LE^ O NUMERO: o ecra escreve a parcela em texto (<c>效果附加分 + %d</c>) mas o
    /// <c>0x0070</c> so' manda o TOTAL do bonus, no +27 — os 44 bytes estao todos atribuidos e
    /// nao sobra campo. E' o cliente que faz a subtraccao, tirando ao total o que o disco e o
    /// all combo valem. Portanto a parcela que se le' no ecra e' leitura directa, nao conta
    /// nossa: basta somar o valor certo ao total.
    ///
    /// **So' contam as casas conhecidas.** Somar os 20 bytes todos apanharia lixo e inventava
    /// milhares do nada.
    /// </summary>
    private static readonly int[] BonusDosFaders = { 0, 4000, 3000, 3000, 4000 };

    private static readonly int[] BonusDoArranjo = { 0, 2000, 2000, 3000 };

    public static int BonusDosEffectores(byte[]? efe)
    {
        if (efe is null) return 0;

        static int Da(int[] tabela, byte[] e, int offset) =>
            offset < e.Length && e[offset] < tabela.Length ? tabela[e[offset]] : 0;

        return Da(BonusDosFaders, efe, Effectores.FadersOffset)
             + Da(BonusDoArranjo, efe, Effectores.ArranjoOffset);
    }

    /// <summary>
    /// O BONUS DO MODO DE VELOCIDADE — a caixa SPEED do ecra de escolha.
    ///
    /// **VEM DO CABECALHO DO 0x00C3, nao do corpo.** Eu tinha-o no `+20` do corpo, por ele valer
    /// 0x0B nas sete corridas normais da highscore_s1 e 0x12 na do SPEED DOWN. Estava errado: o
    /// `+20` vale 5, 11, 12, 14 e 16 por essas gravacoes fora, e sobretudo **o OFF e o SPEED UP
    /// tem o corpo byte a byte igual** (`+0=0`, `+20=11`) e pagam 0 e 2000. Um campo que nao
    /// distingue dois casos com bonus diferente nao pode ser o campo.
    ///
    /// Quem os distingue e' o <see cref="Velocidade.IndiceOffset"/> do cabecalho:
    ///
    /// | indice | caixa SPEED | bonus |
    /// |---|---|---|
    /// | 0 | desligada | 0 |
    /// | 1..8 | multiplicador (x1, x2, ...) | 0 |
    /// | 9 | SPEED DOWN | 3000 |
    /// | 10 | SPEED UP | 2000 |
    /// | 11 | SPEED CHAOS X | 5000 |
    /// | 12 | SPEED BAT | 4000 |
    ///
    /// MEDIDO em duas capturas independentes, com o `微风祈愿` em EZ e a caixa SPEED como unica
    /// coisa a mudar: a speed_modos_s1 tem as cinco corridas (0, 10, 12, 11, 9) e a highscore_s1
    /// confirma-o de fora — sete corridas de indice 2 sem bonus de velocidade nenhum e a oitava,
    /// a do SPEED DOWN, com indice 9 e os mesmos 3000.
    ///
    /// O `scroll` do cabecalho anda agarrado ao indice (9→4, 10→16, 11→0, 12→3, 2→9), igual nas
    /// duas capturas: e' derivado do modo, nao uma escolha a' parte.
    ///
    /// Tudo o que esta' fora da tabela vale zero.
    /// </summary>
    public const int VelocidadeDown = 9;
    public const int VelocidadeUp = 10;
    public const int VelocidadeChaosX = 11;
    public const int VelocidadeBat = 12;

    public static int BonusDaVelocidade(int indice) => indice switch
    {
        VelocidadeDown => 3000,
        VelocidadeUp => 2000,
        VelocidadeChaosX => 5000,
        VelocidadeBat => 4000,
        _ => 0,
    };

    /// <summary>O nome da caixa SPEED, para o registo.</summary>
    public static string? NomeDaVelocidade(int indice) => indice switch
    {
        VelocidadeDown => "SPEED DOWN",
        VelocidadeUp => "SPEED UP",
        VelocidadeChaosX => "SPEED CHAOS X",
        VelocidadeBat => "SPEED BAT",
        _ => null,
    };

    public static (ushort Codigo, int Bonus) Disco(float precisao, int breaks)
    {
        if (breaks == 0)                       // a familia MAX exige combo perfeito
        {
            if (precisao >= 100.0f) return (1, 40000);    // STEEL MAX
            if (precisao >= 99.85f) return (6, 30000);    // GOLDEN MAX
            if (precisao >= 99.75f) return (7, 25000);    // SILVER MAX
            if (precisao >= 98.40f) return (8, 20000);    // BRONZE MAX
        }

        // Abaixo da familia MAX o combo perfeito paga-se a' parte. Ver a tabela acima.
        int allCombo = breaks == 0 ? BonusAllCombo : 0;
        if (precisao >= 96.0f) return (9, 5000 + allCombo);    // GOLDEN DISC
        if (precisao >= 93.0f) return (10, 3000 + allCombo);   // SILVER DISC
        if (precisao >= 90.0f) return (11, 1000 + allCombo);   // BRONZE DISC
        if (precisao >= 88.0f) return (12, allCombo);          // escalao de letra, sem disco
        if (precisao >= 80.0f) return (13, allCombo);
        if (precisao >= 70.0f) return (14, allCombo);
        return (17, allCombo);
    }
}

public static class StageResult
{
    public const ushort ReportId = 0x006F;   // cliente -> servidor
    public const ushort ScreenId = 0x0070;   // servidor -> cliente

    /// <summary>
    /// A MUSICA a que o ecra de resultados diz respeito, no CABECALHO em [4..5] (u16).
    ///
    /// Descoberto comparando os dois fechos gravados do "Let's Begin": o primeiro traz
    /// 0x7E = 126 e o segundo 0x7B = 123, que sao exatamente as suas duas primeiras
    /// musicas. Sem o reescrever, o ecra de resultados de cada etapa do course anuncia a
    /// musica que estava na gravacao e nao a que o jogador acabou de tocar.
    /// </summary>
    public const int ScreenSongOffset = 4;

    public static bool PodeMarcarMusica(byte[] cabecalho) => cabecalho.Length >= ScreenSongOffset + 2;

    public static void MarcarMusica(byte[] cabecalho, uint musica) =>
        BitConverter.TryWriteBytes(cabecalho.AsSpan(ScreenSongOffset, 2), (ushort)musica);

    // no que o cliente reporta
    public const int ReportTotalNotes = 2;    // u16
    public const int ReportEndCombo = 36;     // u32
    public const int ReportMaxCombo = 40;     // u32
    public const int ReportMaxGain = 44;      // u16

    // no que o ecra mostra
    public const int ScreenMax = 4;
    public const int ScreenBreak = 6;
    public const int ScreenCombo = 8;
    public const int ScreenEndCombo = 10;
    public const int ScreenComboAgain = 14;
    public const int ScreenBaseScore = 23;    // u32
    public const int ScreenBonus = 27;        // u32
    public const int ScreenAccuracy = 31;     // float
    public const int ScreenDisc = 35;
    public const int ScreenXpGain = 37;
    public const int ScreenMaxGain = 39;

    /// <summary>
    /// SUBIU DE NIVEL: vale 1 na musica em que o jogador sobe, 0 em todas as outras. E' isto
    /// que faz aparecer o pop-up "您已经达到了等级 N。HP提升 M。" no ecra de resultado.
    ///
    /// Medido em 40 mensagens de resultado espalhadas por 14 gravacoes: vale 1 em duas, e as
    /// duas sao subidas de nivel — a da course_s1 (nivel 9 para 10, com o print do jogador ao
    /// lado) e a da free_s1, cujo 0x0025 fica com o XP exactamente a zero, que e' o que
    /// acontece quando um nivel acaba de virar.
    ///
    /// NAO E' O 0x0026. O <c>OnUpdateUserPropertyLevelInf</c> existe na lista de nomes do
    /// cliente e tem tamanho na tabela dele (25 bytes), mas o servidor real NUNCA o manda:
    /// nao aparece numa unica das 36 gravacoes, incluindo a que tem a subida de nivel. O que a
    /// subida acrescenta ao fecho da musica sao outras duas mensagens — o <c>0x002E</c> da
    /// caixa de presentes e o <c>0x0039</c> do aviso no topo — e este byte.
    /// </summary>
    public const int ScreenSubiuNivel = 36;

    /// <summary>
    /// ALL COMBO: 1 quando a etapa nao teve um unico BREAK, 0 quando teve.
    ///
    /// Medido nos quatro fechos das duas gravacoes de course, sem excecao:
    ///
    ///   course_s1   etapa 1  BREAK 0  -> 01      etapa 2  BREAK 0  -> 01
    ///   course2_s1  etapa 1  BREAK 0  -> 01      etapa 2  BREAK 2  -> 00
    ///
    /// NAO e' "etapa passada": a etapa 2 da course2_s1 tem 00 e o course seguiu para a
    /// terceira, portanto foi passada na mesma.
    ///
    /// Era replicado da gravacao, o que dava all combo a quem tinha falhado notas e tirava-o
    /// a quem nao falhou nenhuma.
    /// </summary>
    public const int ScreenAllCombo = 22;

    /// <summary>
    /// FIM DE COURSE. A ultima etapa traz tres marcas que as do meio nao tem, e os numeros
    /// passam a ser os do COURSE INTEIRO em vez dos da musica.
    ///
    /// Medido na course3_s1 (o "Fine Day", jogado ate' ao COURSE SUCCESS) contra o ecra final:
    ///
    ///     etapa 1   00 00 00 FF ...
    ///     etapa 2   00 00 00 FF ...
    ///     etapa 3   00 01 00 32 ...      <- +1 = 1, +3 = 0x32 = 50 = MaxPrice do course
    ///
    /// E o ultimo byte, +43, passa de 02 para 03.
    ///
    /// Os campos batem todos com o Total Result do print: BREAK 1 (+6), COMBO 730 (+14),
    /// 665726 + 40000 = 705726 (+23 e +27), 98.19% (+31), MAX 56 (+39).
    ///
    /// E o fecho da ultima etapa e' o COMPLETO — 0x002A 0x0025 0x0070 0x006B 0x005E. A ideia
    /// de que o 0x005E fazia aparecer uma quarta musica fantasma estava errada; vinha de
    /// antes de o indice de etapa (+14 do bloco) estar correcto.
    /// </summary>
    public const int ScreenFimDeCourse = 1;

    /// <summary>Preco do course (MaxPrice); 0xFF nas etapas do meio. Ver <see cref="ScreenFimDeCourse"/>.</summary>
    public const int ScreenPrecoCourse = 3;

    /// <summary>Vale 01 numa etapa FALHADA, 02 nas do meio que passaram e 03 na ultima.</summary>
    /// <summary>
    /// NOVO RECORDE: vale 1 quando a pontuacao final bate a melhor do jogador, 0 nas outras.
    /// E' o que faz aparecer a faixa "NEW RECORD!" por cima do numero no ecra de resultado.
    ///
    /// **O RECORDE E' DO MODO E DO CANAL, NAO DA MUSICA.** Um so' numero para todo o free mode
    /// de 5K, outro para o free mode de 7K, e os do ranking a' parte — sao as quatro caixas do
    /// perfil. Nao ha' recorde por musica: o campo 我的最高得分 do ecra mostra o mesmo numero
    /// seja qual for a musica que se acabou de jogar, desde que seja no mesmo modo e canal.
    ///
    /// Medido em 8 fechos da highscore_s1, dois deles com a faixa:
    ///
    ///   #  musica     final    +41   ecra
    ///   0  A.I        207841    1    NEW RECORD  (estreia o recorde)
    ///   1  A.I        247733    1    NEW RECORD  (bate os 207841)
    ///   2  1st-sync   198995    0    mostra "melhor: 247733" — o recorde do A.I
    ///   3  1st-sync   186856    0
    ///   4  1st-sync   183386    0
    ///   5  1st-sync   241689    0    o mais alto dos seis, e mesmo assim nao chega
    ///   6  1st-sync   240306    0
    ///   7  1st-sync   182426    0
    ///
    /// O #5 e' que fecha o argumento: 241689 seria recorde da 1st-sync com folga — o que ele
    /// nao bate e' os 247733 que o A.I deixou. Se houvesse recorde por musica tinha a faixa.
    ///
    /// A captura e' toda de free mode em 5K, portanto o que ela mede e' que o recorde atravessa
    /// MUSICAS. Que nao atravessa canais nem modos ve-se no perfil, que tem uma caixa para cada
    /// um — e' o que <see cref="UserProperty.RecordeOffset"/> e os seus vizinhos ja' guardavam.
    ///
    /// O numero em si ja' viajava, no <see cref="UserProperty.RecordeOffset"/> do 0x0025 da
    /// mesma rajada; o que faltava era a marca.
    /// </summary>
    public const int ScreenNovoRecorde = 41;

    public const int ScreenTipoFecho = 43;

    /// <summary>
    /// A barra no ecra de resultados, float. Vale 100.0 em qualquer etapa que passe e 0.0
    /// numa que va' abaixo. Ver <see cref="MarcarEtapaFalhada"/>.
    /// </summary>
    public const int ScreenBarraOffset = 18;

    /// <summary>
    /// COURSE FAILED: o valor de <see cref="ScreenPrecoCourse"/> quando o course chega ao fim
    /// sem cumprir as condicoes. E' o mesmo que as etapas do meio levam.
    /// </summary>
    public const byte PrecoDeCourseFalhado = 0xFF;

    /// <summary>
    /// Marca a ULTIMA etapa de um course. O <paramref name="passou"/> decide entre
    /// COURSE SUCCESS e COURSE FAILED.
    ///
    /// **MEDIDO** em `gravacoes/curso_falhado.txt` — o "Fine Day" jogado ate' ao fim no
    /// servidor original com 28,26% de precisao, abaixo dos 80% que ele exige — contra o fim
    /// do `course3_s1`, que e' o MESMO course passado. Dos 44 bytes do corpo, os que decidem
    /// sao dois:
    ///
    /// | | +3 | +43 |
    /// |---|---|---|
    /// | etapa do meio | 0xFF | 2 |
    /// | ultima etapa, PASSOU | MaxPrice (0x32 = 50) | 3 |
    /// | ultima etapa, FALHOU | 0xFF | 2 |
    /// | etapa falhada (game over) | 0xFF | 1 |
    ///
    /// Um course falhado leva portanto os MESMOS dois valores de uma etapa do meio; quem diz
    /// que e' o fim e' o <see cref="ScreenFimDeCourse"/>, que vale 1 nos dois casos e 0 no meio.
    ///
    /// O resto do Total Result ACUMULA na mesma — notas, BREAK, combo, precisao e pontuacao
    /// vao os do course inteiro, e batem com o que o ecra mostrou (BREAK 16, COMBO 325,
    /// 28,26%, 217198). O que nao ha' e' bonus: o `0x0089` nao sai e o MAX final e' a soma
    /// crua das etapas (2+4+3=9), sem o +30% de conclusao do course.
    /// </summary>
    public static void MarcarFimDeCourse(byte[] screen, int preco, bool passou = true)
    {
        if (screen.Length <= ScreenTipoFecho) return;
        screen[ScreenFimDeCourse] = 1;
        screen[ScreenPrecoCourse] = passou ? (byte)Math.Clamp(preco, 0, 255) : PrecoDeCourseFalhado;
        screen[ScreenTipoFecho] = (byte)(passou ? 3 : 2);
    }

    /// <summary>
    /// O **Total Result** do ecra COURSE SUCCESS: na ultima etapa estes campos deixam de ser
    /// os da musica e passam a ser os do COURSE INTEIRO.
    ///
    /// Cada coluna acumula a' sua maneira, e a maneira nao e' a mesma para todas. Medido nas
    /// tres gravacoes de course completo — course_s1, course2_s1 e course3_s1 — pondo as
    /// etapas do meio (+43=2) ao lado da ultima (+43=3):
    ///
    /// | coluna | como acumula | course_s1 | course2_s1 | course3_s1 |
    /// |---|---|---|---|---|
    /// | notas (+4) | SOMA | 122+148+132 = 402 | 122+146+132 = 400 | 262+312+402 = 976 |
    /// | BREAK (+6) | SOMA | 0 | 0+2+0 = 2 | 0+0+1 = 1 |
    /// | 最后得分 (+23) | SOMA | 210980+213183+211880 = 636043 | 621650 | 665726 |
    /// | bonus (+27) | SOMA | 40000+30000+40000 = 110000 | 40000 | 40000 |
    /// | 准确度 (+31) | MEDIA SIMPLES | (100,00+99,9324+100,00)/3 | ... | ... |
    /// | MAX (+39) | SOMA + bonus do course | 43 + 30% = 56 (course3_s1) | | |
    /// | COMBO (+14) | ja' vem certo do CLIENTE | 402 | 278 | 730 |
    ///
    /// **O COMBO NAO SE ACUMULA AQUI, e e' importante nao o fazer:** o cliente ja' conta a
    /// corrente ATRAVES das etapas. No relatorio dele (0x006F +40) o "Fine Day" da' 262, 574 e
    /// 730 — o 574 e' 262+312 e o 730 e' 574 mais os 156 que a terceira musica levou ate' ao
    /// break. Somar por cima disso duplicava a conta.
    ///
    /// **A PRECISAO E' MEDIA SIMPLES, nao pesada pelas notas.** A course_s1 decide-o sozinha:
    /// as duas primeiras etapas deram 100,0000 e 99,9324, a terceira levou STEEL MAX (que exige
    /// 100,00), e o valor final gravado e' 99,977478. A media simples da' 99,977475 — bate a'
    /// quinta casa. A media pesada pelas notas daria 99,9751, que nao e' o que la' esta'.
    /// </summary>
    public static void MarcarTotaisDoCourse(byte[] screen, int notas, int pontuacao, int bonus,
                                            float precisao)
    {
        if (screen.Length < ScreenMaxGain + 2) return;
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenMax, 2), (ushort)Math.Clamp(notas, 0, ushort.MaxValue));
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenBaseScore, 4), (uint)Math.Max(0, pontuacao));
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenBonus, 4), (uint)Math.Max(0, bonus));
        if (precisao > 0) BitConverter.TryWriteBytes(screen.AsSpan(ScreenAccuracy, 4), precisao);
    }

    /// <summary>
    /// Marca a etapa como FALHADA. Servir os valores de uma etapa passada fechava o cliente
    /// a seguir ao "continue" — e' o unico sitio onde o meu 0x0070 diferia do real em campos
    /// que nao sao dados de jogo.
    ///
    /// Medido pondo o meu 0x0070 ao lado do da fail_s1, no MESMO course, MESMA musica e
    /// MESMA etapa. Dos 44 bytes do corpo diferiam 14; doze eram MAX, BREAK, combo e
    /// precisao — a jogada, que e' suposto diferir. Os outros dois eram estes:
    ///
    /// | | +18 (float) | +43 |
    /// |---|---|---|
    /// | etapa passada, meio do course | 100.0 | 2 |
    /// | ultima etapa (course completo) | 100.0 | 3 |
    /// | etapa falhada | 0.0 | 1 |
    ///
    /// Nove amostras nas quatro gravacoes de course, sem excepcao.
    /// </summary>
    public static void MarcarEtapaFalhada(byte[] screen)
    {
        if (screen.Length <= ScreenTipoFecho) return;
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenBarraOffset, 4), 0f);
        screen[ScreenTipoFecho] = 1;

        // E NAO SE GANHA NADA. No mesmo diff, o real tinha zero em +35 e +37 (u16 cada) e o
        // meu 17 e 36. O +39 batia nos dois, por isso fica como esta'.
        BitConverter.TryWriteBytes(screen.AsSpan(35, 2), (ushort)0);
        BitConverter.TryWriteBytes(screen.AsSpan(37, 2), (ushort)0);
    }

    public static bool CanReadReport(byte[] body) => body.Length >= ReportMaxGain + 2;
    public static bool CanPatchScreen(byte[] body) => body.Length >= ScreenMaxGain + 2;

    /// <summary>
    /// Passa para o ecra os campos que se sabem desta jogada.
    ///
    /// O combo e o MAX ganho vem do relatorio do cliente; o BREAK e' contado pelas quedas
    /// de HP durante a musica (ver <see cref="PlayState"/>) e o MAX sai da identidade
    /// <c>total de notas = MAX + BREAK</c>, verificada em nove jogadas.
    /// </summary>
    public static (int MaxCombo, int Max, int Break) Apply(byte[] screen, byte[] report, int breaks,
                                                           float precisao, int xpGanho,
                                                           byte[]? effectores = null,
                                                           int modoVelocidade = 0)
    {
        // O XP que o ecra anuncia tem de ser o mesmo que o perfil soma. Sem isto o jogador
        // ve' um numero no ecra de resultados e outro na barra de progresso.
        //
        // NOTA: o valor real varia com a jogada (observados 47, 33, 31, 29, 32 no servidor
        // original) e e' calculado pelo servidor; aqui vai o que o GrooveServer atribui,
        // que e' fixo. E' o proximo cabo solto desta zona.
        if (xpGanho > 0) BitConverter.TryWriteBytes(screen.AsSpan(ScreenXpGain, 2), (ushort)xpGanho);

        if (precisao > 0) BitConverter.TryWriteBytes(screen.AsSpan(ScreenAccuracy, 4), precisao);

        int maxCombo = (int)BitConverter.ToUInt32(report, ReportMaxCombo);
        int endCombo = (int)BitConverter.ToUInt32(report, ReportEndCombo);
        int maxGain = BitConverter.ToUInt16(report, ReportMaxGain);
        int total = BitConverter.ToUInt16(report, ReportTotalNotes);

        BitConverter.TryWriteBytes(screen.AsSpan(ScreenCombo, 2), (ushort)maxCombo);
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenComboAgain, 2), (ushort)maxCombo);
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenEndCombo, 2), (ushort)endCombo);
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenMaxGain, 2), (ushort)maxGain);

        int acertadas = Math.Max(0, total - breaks);
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenMax, 2), (ushort)acertadas);
        BitConverter.TryWriteBytes(screen.AsSpan(ScreenBreak, 2), (ushort)breaks);
        if (screen.Length > ScreenAllCombo) screen[ScreenAllCombo] = (byte)(breaks == 0 ? 1 : 0);

        if (precisao > 0 && total > 0)
        {
            int baseScore = ScoreFormula.Base(total, precisao, maxCombo, breaks);
            var (disco, bonus) = ScoreFormula.Disco(precisao, breaks);
            // O bonus dos effectores soma por cima do disco e do all combo — E O DA CAIXA
            // SPEED TAMBEM. Eu tinha-o somado so' na conta do recorde e nao aqui, e o ecra
            // ficava a mostrar menos do que o jogador tinha ganho: SPEED BAT com FADER OUT e
            // 5K RANDOM anunciava 6000 em vez de 10000, que sao os dois effectores sem a
            // velocidade. As duas contas tem de usar as mesmas parcelas.
            bonus += ScoreFormula.BonusDosEffectores(effectores)
                   + ScoreFormula.BonusDaVelocidade(modoVelocidade);
            BitConverter.TryWriteBytes(screen.AsSpan(ScreenBaseScore, 4), (uint)baseScore);
            BitConverter.TryWriteBytes(screen.AsSpan(ScreenBonus, 4), (uint)bonus);
            BitConverter.TryWriteBytes(screen.AsSpan(ScreenDisc, 2), disco);
        }
        return (maxCombo, acertadas, breaks);
    }
}

public static class RequestId
{
    /// <summary>Cliente compra um item; o id do catalogo vai no cabecalho, bytes 3..6.</summary>
    public const ushort PurchaseItemReq = 0x00DD;

    /// <summary>Servidor devolve a lista de itens possuidos.</summary>
    public const ushort PurchaseItemAck = 0x00DE;

    /// <summary>Cliente vende um item: catalogo no cabecalho[3..6], instancia no corpo[0..3].</summary>
    public const ushort SellItemReq = 0x00DF;

    /// <summary>Servidor confirma a venda; lista de itens em +2, MAX no cabecalho[5..6].</summary>
    public const ushort SellItemAck = 0x00E0;

    /// <summary>
    /// Cliente APAGA um item do inventario. Tem a forma exata do <see cref="SellItemReq"/>:
    /// 19 bytes, catalogo no cabecalho[3..6] e instancia no corpo[0..3].
    ///
    /// Medido em captures/7k_02.pcapng, onde o pedido
    /// <c>DB006F02A40100 / C72735010000000000000000</c> apagou o catalogo 107522, instancia
    /// 20261831 — o quarto item do inventario que o <c>0x0044</c> do mesmo login listava.
    ///
    /// **A IDENTIDADE DE UM ITEM E' O PAR (catalogo, instancia), nao a instancia.** Nesse
    /// mesmo inventario ha' dois itens com a instancia 20261831 e catalogos diferentes
    /// (99332 e 107522). Apagar so' pela instancia levava os dois.
    /// </summary>
    public const ushort DeleteItemReq = 0x00DB;

    /// <summary>
    /// Servidor confirma o apagar; 245 bytes, lista de itens em +6.
    ///
    /// O proprio cliente confirma a leitura: a tabela de handlers extraida dele chama-lhe
    /// <c>OnDeleteItemAck</c> (ver docs/protocolo-mensagens.md).
    /// </summary>
    public const ushort DeleteItemAck = 0x00DC;

    /// <summary>Cliente equipa um item; a instancia vai nos bytes 0..3 do corpo.</summary>
    public const ushort MountItemReq = 0x00D7;

    /// <summary>Servidor confirma: `[slot:u16][instancia:u32]`.</summary>
    public const ushort MountItemAck = 0x00D8;

    /// <summary>Cliente pede o arranque da musica; a resposta e' o grupo por musica.</summary>
    public const ushort StartReq = 0x005F;

    /// <summary>Cliente anuncia a musica escolhida; o id vai em claro nos bytes 3..6.</summary>
    public const ushort ChangeDiscReq = 0x0076;

    /// <summary>
    /// Cliente USA um descartavel (booster). 12 bytes, medidos na tabela do cliente.
    ///
    /// NUNCA FOI CAPTURADO — nenhuma das gravacoes tem um `0x00BA`, porque nunca se gastou um
    /// booster com o Wireshark a correr. O que se sabe vem todo da tabela de tamanhos que o
    /// proprio cliente traz (`docs/tamanhos-do-cliente.txt`, tirada do despejo de memoria) e
    /// de um padrao que se repete tres vezes seguidas nos pedidos de item:
    ///
    /// | pedido | ack (pedido+1) | fail (pedido+2) |
    /// |---|---|---|
    /// | `0x00B4` GetItemReq | `0x00B5`, 11 B | `0x00B6`, 15 B |
    /// | `0x00B7` ItemLevelUpReq | `0x00B8`, 11 B | `0x00B9`, 14 B |
    /// | `0x00BA` UseItemReq | `0x00BB`, **3 B** | `0x00BC`, 24 B |
    ///
    /// Tres bytes e' so' cabecalho: id e byte de chave, sem corpo e sem cifra (a cifra so'
    /// entra a partir dos 8). Ou seja o ack e' um "esta' bem" pelado, e da'-se para construir
    /// sem gravacao nenhuma.
    ///
    /// O ID DO CATALOGO assume-se em `[3..6]`, como no <see cref="PurchaseItemReq"/> e no
    /// <see cref="SellItemReq"/>. E' o unico ponto por confirmar, e confirma-se sozinho: se
    /// estiver errado o registo mostra um catalogo que nao existe na tabela de itens.
    /// </summary>
    public const ushort UseItemReq = 0x00BA;

    /// <summary>Servidor confirma o uso. So' cabecalho, 3 bytes. Ver <see cref="UseItemReq"/>.</summary>
    public const ushort UseItemAck = 0x00BB;
}

public enum MessageId : ushort
{
    /// <summary>Saudacao inicial do servidor: 3 bytes `03 00 cc`, sempre em claro.</summary>
    ServerHello = 0x0003,

    /// <summary>
    /// DJMaxNet::ConnectReq (cliente) e DJMaxNet::OnConnectAck (servidor).
    /// Partilham o mesmo id. O Ack transporta a chave de sessao no offset 7.
    /// </summary>
    Connect = 0x000A,

    /// <summary>Observado no fluxo de autenticacao (cliente -> servidor), 67 bytes.</summary>
    AuthenticateIn = 0x0011,

    /// <summary>Resposta observada ao <see cref="AuthenticateIn"/>, 92 bytes.</summary>
    AuthenticateInAck = 0x0010,

    /// <summary>DJMaxNet::LogInReq — 53 bytes; copia a chave de sessao para o offset 13.</summary>
    LogInReq = 0x001B,

    /// <summary>Resposta observada ao login no servidor de jogo, 74 bytes.</summary>
    LogInAck = 0x0020,

    /// <summary>Observado (servidor -> cliente), 239 bytes, e (cliente -> servidor) 15 bytes.</summary>
    ChannelInfo = 0x002F,

    /// <summary>Keepalive observado: 3 bytes, em claro, em ambas as direcoes.</summary>
    KeepAlivePing = 0x0007,

    /// <summary>Resposta observada ao ping: 3 bytes, em claro.</summary>
    KeepAlivePong = 0x0006,
}

/// <summary>Codigos de resultado, retirados das strings do cliente.</summary>
public static class ResultCode
{
    /// <summary>Verificado em FUN_004319d0: `if (*(short*)(pkt+3) == 5)` aceita o ConnectAck.</summary>
    public const ushort ConnectSuccess = 5;

    /// <summary>Verificado em FUN_00432cd0: `if (*(short*)(pkt+7) != 0x2b)` rejeita o login.</summary>
    public const ushort LoginSuccess = 0x2B;

    /// <summary>Faz o cliente reconectar — string "Excesspeer-&gt;Reconnect".</summary>
    public const ushort ExcessPeerReconnect = 0x31;
}
