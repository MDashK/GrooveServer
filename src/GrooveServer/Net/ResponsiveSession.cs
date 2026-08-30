using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Net;

/// <summary>
/// Serve o cliente respondendo a cada pedido, em vez de repetir um guiao por ordem.
///
/// Diferenca em relacao ao <see cref="ReplaySession"/>: aqui o servidor gera a sua
/// propria chave de sessao e cifra tudo, o que lhe permite responder a qualquer pedido
/// em qualquer altura. O jogador deixa de ter de repetir a sessao gravada â€” carregar em
/// ESC passa a devolver um <c>LeaveRoomAck</c>, e nao a nada.
///
/// As respostas vem do <see cref="ResponseMap"/> (o que o servidor original respondeu a
/// cada tipo de pedido), guardadas em claro e recifradas com a chave desta sessao.
/// </summary>
public sealed class ResponsiveSession : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private ResponseMap _map;

    /// <summary>
    /// A outra gravacao do par auth/jogo. O cliente liga-se DUAS vezes por login: primeiro
    /// para autenticar (0x0011), depois para jogar (0x001B). Ate' agora escolhia-se pela
    /// ORDEM da ligacao — a primeira era auth, as seguintes jogo — o que parte assim que o
    /// jogador fecha e reabre o cliente sem reiniciar o servidor: a nova ligacao de
    /// autenticacao recebia o mapa de jogo, o 0x0011 ficava sem resposta e o cliente
    /// encalhava em "a confirmar a conta".
    ///
    /// Passa a decidir-se pelo PRIMEIRO PEDIDO, que e' quem sabe do que se trata.
    /// </summary>
    private readonly ResponseMap? _outroMapa;

    private bool _mapaEscolhido;
    private readonly IPAddress _advertise;
    private readonly int _id;
    private readonly Action<string> _log;

    /// <summary>O cliente anuncia o resultado da musica que acabou de tocar.</summary>
    private const ushort StageResultInf = 0x006F;

    /// <summary>Servidor confirma o arranque; o cabecalho diz se e' gameplay ou music video.</summary>
    private const ushort StartInf = 0x0060;

    /// <summary>Estado periodico durante o jogo.</summary>
    private const ushort PeriodicState = 0x0072;

    /// <summary>Cliente avisa que a musica terminou. E' o gatilho do fecho.</summary>
    private const ushort PlayOverReq = 0x006A;

    /// <summary>Servidor fecha a jogada; vem no conjunto que devolve o jogador a' lista.</summary>
    private const ushort PlayOverInf = 0x006B;

    /// <summary>Cliente pede para saltar a musica (e' o que o ESC envia).</summary>
    private const ushort PlaySkipReq = 0x0067;

    /// <summary>Servidor confirma o salto. So' faz sentido como resposta ao pedido.</summary>
    private const ushort PlaySkipInf = 0x0068;

    /// <summary>Relatorio de progresso durante a musica, dezenas de vezes por jogada.</summary>
    private const ushort PlayStateInf = 0x006C;

    /// <summary>Pedido anti-batota; e' a unica resposta legitima ao <see cref="PlayStateInf"/>.</summary>
    private const ushort CheckDataReq = 0x0071;

    /// <summary>
    /// Mensagens do cliente que sao CONFIRMACOES ou BATIMENTOS, com a lista fechada do que
    /// o servidor real lhes responde.
    ///
    /// O ResponseMap agrupa respostas por proximidade temporal, por isso tudo o que o
    /// servidor difunda por iniciativa propria (salas do lobby, keepalives, e ate' o fecho
    /// de musica) acaba no balde de quem calhou de falar antes. Nestas mensagens isso e'
    /// garantidamente errado: o cliente manda-as aos molhos durante a jogada, e sao as que
    /// mais apanham lixo.
    ///
    /// Apareceu seis vezes, sempre igual e sempre a custar uma ronda de testes: AliveReq,
    /// PlaySkipInf, 0x0004 com um 0x003A, 0x006C com o fecho de musica inteiro, e agora o
    /// 0x0006 e o 0x0072 na gravacao nova. Tirar uma mensagem de cada vez resolve o caso e
    /// deixa a familia; a lista branca resolve a familia.
    /// </summary>
    private static readonly Dictionary<ushort, ushort[]> RespostasLegitimas = new()
    {
        [ClientAck] = Array.Empty<ushort>(),      // confirmacao generica: nunca leva resposta
        [0x0006] = new[] { AliveReq },            // pong do keepalive; so' o proximo ping
        [PlayStateInf] = new[] { CheckDataReq },  // batimento de estado durante a musica
        [0x0072] = new[] { CheckDataReq },        // resposta ao anti-batota
    };

    /// <summary>Keepalive do servidor. Temporizado, nunca em resposta a nada.</summary>
    private const ushort AliveReq = 0x0007;

    /// <summary>Confirmacao generica do cliente (3 B, em claro). Nunca leva resposta.</summary>
    private const ushort ClientAck = 0x0004;

    /// <summary>Intervalo do keepalive, medido no servidor original.</summary>
    private static readonly TimeSpan AliveInterval = TimeSpan.FromSeconds(50);

    /// <summary>O cliente anuncia a musica escolhida; o id vai em claro no cabecalho.</summary>
    private const ushort ChangeDiscReq = 0x0076;

    /// <summary>Configuracao do jogo; o id da musica esta' nos bytes 4..7 do corpo.</summary>
    private const ushort GameInfoInf = 0x007A;

    /// <summary>
    /// Reescrever o id da musica no GameInfoInf. Desligado: comprovadamente crasha o
    /// cliente, porque o id indexa o bloco de 18 KB que vai a seguir e nao pode ser
    /// mudado isoladamente. Mantido para poder voltar a testar quando o bloco for
    /// compreendido.
    /// </summary>
    public static bool EchoSelectedSong { get; set; }

    private DjMaxCipher? _outgoing;
    private readonly Dictionary<ushort, int> _seen = new();
    private bool _stageFinished;
    private bool _musicVideo;
    private DateTime _lastAlive = DateTime.UtcNow;

    /// <summary>Pedido que abriu o ecra atual; e' ele que desambigua o 0x00F0.</summary>
    private ushort _lastScreen;

    /// <summary>"Da-me os dados do ecra" — o mesmo id para a sala, a loja e o inventario.</summary>
    private const ushort ScreenDataReq = 0x00F0;

    private const ushort CreateRoomReq = 0x004C;

    /// <summary>"Continue" no ecra de game over de um course; a resposta e' o 0x0088.</summary>
    private const ushort ContinueCourseReq = 0x0087;
    private const ushort ContinueCourseAck = 0x0088;
    private const ushort OpenShopReq = 0x012C;
    private const ushort ShopDataInf = 0x012D;
    /// <summary>Inventario da conta, enviado no login; 744 bytes de corpo.</summary>
    private const ushort InventoryInfoInf = 0x0044;

    /// <summary>A lista de discos que fecha cada musica. Ver Protocol.DefaultItems.</summary>
    private const ushort UpdInvDefaultItemInf = 0x002A;
    /// <summary>Cliente pede a atualizacao do icone depois de equipar.</summary>
    private const ushort UserIconReq = 0x0023;
    private const ushort UserIconInf = 0x0024;

    /// <summary>
    /// Indice do jogador na lista do lobby, lido do <c>WaiterInfoUpdateInf</c> que esta
    /// sessao enviou. O <c>UpdateUserIconInf</c> tem de levar o MESMO valor, senao o
    /// cliente atualiza o avatar de outra entrada e o do jogador nunca muda.
    /// </summary>
    private ushort _waiterIndex;

    /// <summary>
    /// O ultimo <c>StageResultInf</c> que o cliente enviou. E' de la' que saem o combo e o
    /// MAX ganho reais desta jogada — sem isto o ecra de resultados mostra os numeros de
    /// quem gravou a captura.
    /// </summary>
    private byte[]? _lastReport;

    /// <summary>
    /// Contagem de breaks desta musica, seguida pelo HP que o CLIENTE vai reportando no
    /// <c>PlayStateInf</c>. O servidor nao assume maximo nenhum: <c>_hp</c> arranca no
    /// primeiro relatorio e serve so' para contar breaks pelas quedas.
    ///
    /// O NUMERO NAO E' O DO PAINEL DE PERFIL. O perfil mostra "HP 195 (130+65)" com a gear
    /// equipada, mas o medidor que o cliente reporta durante a musica arranca em 130,00 e nao
    /// passa dai' — igual com a gear de 30 dias, que da' +130. O bonus de HP dos itens esta'
    /// no painel e nao neste medidor; se faz alguma coisa em jogo, e' dentro do cliente.
    /// </summary>
    private float? _hp;
    private int _breaks;

    /// <summary>Precisao acumulada, do ultimo <c>PlayStateInf</c> da musica.</summary>
    private float _precisao;

    /// <summary>
    /// Primeiro id de catalogo que ja' nao e' avatar. Os itens vistos ate' agora agrupam-se
    /// em gamas: avatares 99332..101382, gear 107545..107556, notas 108056..108574. Serve
    /// so' para adivinhar qual dos itens equipados vai para o icone do lobby quando o
    /// cliente ainda nao o disse; assim que ele mande um 0x0023, e' esse que manda.
    /// </summary>
    private const uint CatalogoGearMin = 107000;

    /// <summary>
    /// Gama de ids de catalogo ja' vistos (99332 a 108574), alargada com folga. Serve para
    /// nao tomar por item qualquer numero que apareca num corpo de mensagem.
    /// </summary>
    private const uint CatalogoMin = 90000;
    private const uint CatalogoMax = 130000;
    /// <summary>
    /// Lista dos courses que existem, enviada ao entrar no modo. So' traz os IDs (u16, base
    /// zero) — nomes, condicoes e premios estao nos ficheiros do cliente, tal como acontece
    /// com as musicas. E' de tamanho VARIAVEL, com o total no cabecalho [3..6], como o
    /// GameInfoInf: 49 bytes para os 21 courses observados (7 + 21x2).
    /// </summary>
    /// <summary>Confirmacao de entrada na sala; identifica o grupo de arranque de sala.</summary>
    private const ushort CreateRoomAck = 0x004D;

    /// <summary>O botao START. Nao leva resposta, mas marca a saida do ecra da sala.</summary>
    private const ushort StartPressed = 0x00C3;

    /// <summary>Registo pessoal da musica, na rajada de arranque. 0xFF = sem registo.</summary>
    private const ushort StartRecordInf = 0x00C9;

    /// <summary>
    /// Byte 2 do corpo do <c>GameInfoInf</c>, por musica, tal como o servidor real o enviou.
    ///
    /// Os blocos colhidos dos ficheiros do jogo estao certos em TUDO menos este byte: de
    /// ~13000 bytes, difere exatamente um, e o cliente fecha-se a carregar. Medido com
    /// `libverify` sobre a end_s1 — 12 dos 13 blocos falham, sempre no offset 2 e sempre
    /// so' nesse.
    ///
    /// O que o campo significa nao esta' decifrado; os valores andam entre 10 e 20 nos dois
    /// lados, por isso nao e' lixo. A documentacao antiga dizia que [2..3] era a constante
    /// 0x000E — era o valor da musica 0, generalizado a partir de uma amostra so'.
    ///
    /// Enquanto nao se souber calcular, usa-se o valor observado. So' cobre as musicas que
    /// aparecem em alguma gravacao.
    /// </summary>
    private static readonly Dictionary<uint, byte> ByteDoisPorMusica = new();

    private const int GameInfoByteDois = 2;

    private const ushort CourseListInf = 0x0082;

    /// <summary>Ranking de um course (2155 B). Resposta ao 0x0083 e parte da do 0x0085.</summary>
    private const ushort CourseRankAck = 0x0084;

    /// <summary>Cliente escolhe um course; o indice vai no cabecalho.</summary>
    private const ushort CourseSelectReq = 0x0083;

    /// <summary>Cliente navega na lista; a resposta traz o ranking do course apontado.</summary>
    private const ushort CourseBrowseReq = 0x0085;

    /// <summary>
    /// O <c>CreateRoomReq</c> serve os dois modos e so' um byte os distingue: <c>+33</c>
    /// vale 4 no course mode e 0 no free mode. Sem isto o <c>0x00F0</c> que vem a seguir
    /// recebe a resposta do modo errado, porque o pedido e' identico nos dois casos.
    /// </summary>
    /// <summary>
    /// QUE MODO A SALA E', no byte +33 do <c>0x004C</c> que o cliente manda.
    ///
    /// Sao tres, e o servidor so' conhecia um: testava-se <c>== 4</c> e tudo o resto caia em
    /// free mode. Uma sala de RANKING passava por free mode e o jogador era atirado para la'.
    ///
    /// O servidor devolve o mesmo numero em dois sitios da rajada da sala, e e' dai' que o
    /// cliente sabe onde esta' — medido nas quatro gravacoes:
    ///
    /// | modo | 0x004C +33 (pedido) | 0x0050 +32 | 0x004D +37 |
    /// |---|---|---|---|
    /// | free | 0 | 0 | 0 |
    /// | ranking | 1 | 1 | 1 |
    /// | course | 4 | 4 | 4 |
    /// </summary>
    private const int RoomTypeOffset = 33;
    private const byte RoomTypeFree = 0;
    private const byte RoomTypeRanking = 1;
    private const byte RoomTypeCourse = 4;

    /// <summary>Onde o tipo de sala vai nas duas respostas. Ver <see cref="RoomTypeOffset"/>.</summary>
    private const ushort RoomDescInf = 0x0050;
    private const int RoomDescTypeOffset = 32;        // corpo do 0x0050
    private const int CreateRoomAckTypeOffset = 37;   // corpo do 0x004D

    /// <summary>O modo da sala em que a sessao esta'. Ver <see cref="RoomTypeOffset"/>.</summary>
    private byte _tipoSala = RoomTypeFree;
    private bool _courseRoom;

    /// <summary>
    /// Qual das musicas do course vai a seguir. Um course e' uma sequencia FIXA de musicas
    /// definida a' partida — o jogador nunca escolhe, e por isso nao ha' ChangeDiscReq.
    /// </summary>
    private int _courseSong;
    private int _courseIndex = -1;

    /// <summary>
    /// Musica que esta' a tocar. Serve para marcar o ecra de resultados com a musica certa
    /// — ver Protocol.StageResult.ScreenSongOffset.
    /// </summary>
    private uint? _musicaEmCurso;

    /// <summary>
    /// A versao que o cliente anunciou no ConnectReq. Decide se os ids de musica precisam de
    /// traducao — ver Protocol.ClientVersion e Net.SongIdMap.
    /// </summary>
    private uint _versaoDoCliente;

    /// <summary>O mapa a usar nesta sessao, ou nulo se o cliente for o nosso.</summary>
    private SongIdMap? _mapaDeMusicas;

    /// <summary>
    /// Total corrido do course: o que foi para o <c>0x0070</c> da etapa anterior em
    /// <see cref="Protocol.StageResult.ScreenEndCombo"/>. Ver Protocol.StartParameter.
    /// </summary>
    private int _totalCorridoCourse;

    /// <summary>Diagnostico: manter as salas gravadas no lobby (--salas-gravadas).</summary>
    public static bool SalasGravadas { get; set; }

    /// <summary>
    /// Servir a lista de itens de omissao A PARTIR DA CONTA em vez da gravacao (--itens-conta).
    ///
    /// DESLIGADO. Em teste piorou: os discos que a conta mostrava desapareceram e o disco
    /// ganho no fim do course continuou na mesma contagem. Duas falhas identificadas:
    ///
    /// 1. a lista e' escrita ORDENADA POR ID e a gravacao tem outra ordem — se o cliente
    ///    mapear posicao para icone, baralha o ecra de coleccao;
    /// 2. o premio e' sorteado NO SERVIDOR, mas os discos ganham-se DURANTE o gameplay e e'
    ///    o cliente que sabe quais — o servidor so' os deve registar. Falta encontrar onde
    ///    e' que o cliente os reporta (candidatos: 0x006F e 0x00E7).
    ///
    /// AMBAS CORRIGIDAS. A ordem passa a ser a do template (ver DefaultItems.Escrever), e o
    /// premio de conclusao vem do DiscNum do course em vez de ser sorteado. Quanto ao disco
    /// de cada etapa, esse E' mesmo do servidor: o cliente nunca o envia — o id que sobe em
    /// cada etapa nao aparece em nenhuma das tres mensagens que ele manda no fim (0x006F,
    /// 0x00E7, 0x0072), nas tres gravacoes de course.
    /// </summary>
    public static bool ItensDaConta { get; set; } = true;

    /// <summary>
    /// A ultima velocidade que o cliente anunciou (0x00C3 ao pedir a musica, 0x00C5 ao mudar
    /// com o F5). Devolvida no cabecalho do 0x00C4. Ver Protocol.Velocidade.
    /// </summary>
    private (ushort Indice, ushort Scroll)? _velocidade;

    /// <summary>BREAKs somados das etapas ja' feitas do course.</summary>
    private int _breaksCourse;

    /// <summary>MAX ganho somado nas etapas do course. Ver o Total Result do ecra final.</summary>
    private int _maxGanhoCourse;

    /// <summary>O XP concedido no course inteiro, para o bonus de conclusao. Ver `exp=`.</summary>
    private int _xpGanhoCourse;

    /// <summary>
    /// As restantes colunas do Total Result, somadas etapa a etapa. Ver
    /// <see cref="Protocol.StageResult.MarcarTotaisDoCourse"/>, que documenta como cada uma
    /// acumula e onde isso foi medido.
    ///
    /// A precisao guarda-se em LISTA e nao em soma porque o que o ecra final mostra e' a media
    /// simples das etapas — para a fazer e' preciso saber quantas foram.
    /// </summary>
    private int _notasCourse;
    private int _pontuacaoCourse;
    private int _bonusCourse;
    private readonly List<float> _precisoesCourse = new();

    /// <summary>
    /// O course chegou ao fim sem cumprir as condicoes de <c>[Clear]</c>.
    ///
    /// Decide-se no fecho da ULTIMA etapa e nao no ecra, porque o premio de item sai antes do
    /// ecra — ver <see cref="DecidirCourse"/>.
    /// </summary>
    private bool _courseFalhado;

    /// <summary>Estado de uma corrida do modo ranking. Ver <see cref="SomarEtapaDeRanking"/>.</summary>
    private int _pontuacaoRanking;
    private int _etapaRanking;

    /// <summary>
    /// Os effectores que o jogador escolheu na lista de musicas, como vieram no corpo do
    /// <c>0x00C3</c>. Devolvidos no corpo do <c>0x00C4</c>. Ver Protocol.Effectores.
    /// </summary>
    private byte[]? _effectores;

    /// <summary>
    /// O XP e o MAX que a ULTIMA etapa concedeu mesmo — ja' com os bonus dos itens equipados.
    /// E' o que o ecra de resultado passa a mostrar, em vez do valor cru que o cliente
    /// reportou. Ver onde sao escritos no 0x0070.
    ///
    /// NULO quando ainda nao houve concessao desde o ultimo ecra. Sem isto, um 0x0070 que
    /// saisse sem o 0x006F correspondente mostrava os numeros da musica ANTERIOR — e pior,
    /// somava-os outra vez ao total do course.
    /// </summary>
    private int? _xpConcedido;
    private int? _maxConcedido;

    /// <summary>Quando saiu o ultimo 0x0084, para espacar o 0x0086 como o servidor real.</summary>
    private DateTime? _ultimo0x0084;

    /// <summary>A etapa que esta' a fechar e' a ultima do course. Ver StageResult.MarcarFimDeCourse.</summary>
    private bool _fimDeCourse;

    /// <summary>O course cuja tabela vai sair a seguir, para a construir com a conta certa.</summary>
    private int _rankCourse = -1;

    /// <summary>
    /// O id que o cliente tem como seu, lido do <c>0x0043</c> do login. Ver
    /// <see cref="Protocol.UserInfo.IdOffset"/>; e' o que identifica o jogador na tabela
    /// de um course.
    /// </summary>
    private ushort _userId;

    /// <summary>O jogador subiu de nivel nesta musica: o fecho leva o 0x0026. Ver Protocol.LevelUp.</summary>
    private bool _subiuDeNivel;

    /// <summary>Molde de tabela de course vazia, procurado uma vez. Ver MoldeDeTabelaVazia.</summary>
    /// <summary>A caixa SPEED do 0x00C3, pelo indice do cabecalho. Paga bonus como um effector.</summary>
    private int _modoVelocidade;

    /// <summary>Se a musica que acabou bateu o recorde do jogador — vai no 0x0070 +41.</summary>
    private bool _novoRecorde;

    private ResponseMap.Message? _moldeVazio;
    private bool _moldeProcurado;

    /// <summary>
    /// Item que o sorteio da conclusao deu, ja' com o id de catalogo completo. Zero quando
    /// nao saiu nada — e nesse caso o <c>0x0089</c> nao chega a sair. Ver Protocol.CoursePrize.
    /// </summary>
    private uint _itemSorteado;

    /// <summary>Pausa medida entre o 0x0084 e o 0x0086 na course2_s1: 207 ms.</summary>
    private static readonly TimeSpan PausaDepoisDoRanking = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Rajada de arranque da PRIMEIRA musica de um course, retida ate' o cliente mandar o
    /// <c>0x00F0</c>. Ver o local onde e' preenchida.
    /// </summary>
    private List<ResponseMap.Message>? _arranqueEmEspera;

    /// <summary>A etapa de course que acabou de fechar foi abaixo (barra a zero).</summary>
    private bool _etapaFalhada;

    /// <summary>O jogador continuou depois de falhar: a proxima etapa e' a MESMA.</summary>
    private bool _repetirEtapa;

    /// <summary>Fecho de etapa retido a' espera do 0x0087. Ver a nota no envio das respostas.</summary>
    private List<ResponseMap.Message>? _fechoEmEspera;

    /// <summary>Back na lista de musicas.</summary>
    private const ushort LeaveRoomReq = 0x0073;
    private const ushort LeaveRoomAck = 0x0074;
    private const ushort SystemInfoReq = 0x00FD;
    private const ushort SystemInfoAck = 0x00FE;

    /// <summary>Troca o nome gravado pelo da conta ativa; null mantem o gravado.</summary>
    public static Protocol.NameRewriter? Names { get; set; }

    /// <summary>
    /// Gravacoes suplementares, consultadas quando a principal nunca viu um pedido.
    /// Serve para juntar sessoes que cobrem partes diferentes do jogo — a de gameplay
    /// nao passou pela loja, a da loja nao jogou musicas.
    /// </summary>
    public static List<ResponseMap> Extras { get; } = new();

    /// <summary>Conta ativa e o seu ficheiro; null serve o inventario gravado.</summary>
    public static UserStore.Account? Account { get; set; }
    public static UserStore? Store { get; set; }

    /// <summary>Perfil vivo desta sessao; null mantem os valores da gravacao.</summary>
    /// <summary>Diagnostico: servir as mensagens gravadas sem reescrever nada do perfil.</summary>
    /// <summary>
    /// Reenvia as gravacoes sem lhes tocar. Ver a nota no <c>SendAsync</c> — e' uma
    /// ferramenta de diagnostico, nao um modo de jogo.
    /// </summary>
    public static bool Replica { get; set; }


    /// <summary>Servir a rajada de course GRAVADA em vez da da biblioteca. Ver a nota no local.</summary>
    public static bool CourseGravado { get; set; }

    /// <summary>Servir o inventario da gravacao em vez do da conta. Ver a nota no local.</summary>
    public static bool InventarioGravado { get; set; }

    /// <summary>
    /// Nome de uma gravacao de onde sai a rajada de LOGIN inteira, mantendo tudo o resto na
    /// principal. Diagnostico: serve para saber se a diferenca entre a sessao em que o
    /// course arranca e aquela em que nao arranca esta' ou nao no login.
    /// </summary>
    public static string? LoginDe { get; set; }

    /// <summary>Diagnostico: servir o ConnectAck desta gravacao. Ver SendConnectAckAsync.</summary>
    public static string? ConnectAckDe { get; set; }

    /// <summary>Diagnostico: forca o +2 do bloco (candidato a velocidade x10). Ver --velocidade.</summary>
    public static byte? Velocidade { get; set; }

    /// <summary>Que musicas compoe cada course. Sem ela, so' se joga o course gravado.</summary>
    public static CourseTable? Courses { get; set; }

    /// <summary>O que cada item da loja faz. Ver ItemTable.</summary>
    public static ItemTable? Itens { get; set; }

    /// <summary>
    /// Traducao de ids de musica para o cliente SNDA de 2007. Ver Net.SongIdMap.
    /// </summary>
    public static SongIdMap? MusicasSnda { get; set; }

    /// <summary>Titulos das musicas, para o log. Nao afeta o protocolo.</summary>
    public static SongCatalog? Catalogo { get; set; }

    /// <summary>
    /// Diagnostico: nao reescrever NADA nas mensagens de fecho de musica (0x0025, 0x0070).
    ///
    /// Serve para responder a "o cliente muda de caminho depois do fecho porque eu reescrevo
    /// alguma coisa, ou apesar disso?". Medido: depois do fecho, o meu cliente envia um
    /// 0x0021 e dois 0x00F0 que o cliente do servidor real nao envia.
    /// </summary>
    public static bool SemFecho { get; set; }

    /// <summary>Diagnostico: nao reescrever o 0x0070 (ecra de resultados).</summary>
    public static bool SemResultados { get; set; }

    /// <summary>Diagnostico: nao reescrever o 0x0025 (perfil) do fecho.</summary>
    public static bool SemPerfilFecho { get; set; }

    public static bool SemPerfil { get; set; }

    /// <summary>Diagnostico: nao reescrever a experiencia.</summary>
    public static bool SemXp { get; set; }

    /// <summary>Diagnostico: nao reescrever o MAX.</summary>
    public static bool SemMax { get; set; }

    public static PlayerProfile? Profile { get; set; }
    private PlayerProfile? _profile => SemPerfil ? null : Profile;
    private Protocol.SongKey? _selected;
    private DjMaxCipher? _incoming;

    private readonly SongLibrary? _songs;

    /// <summary>
    /// O canal desta sessao, <c>"5k"</c> ou <c>"7k"</c>. Vem do <see cref="GameServer.Canal"/>.
    ///
    /// Serve para separar as tabelas de high score dos courses: o mesmo course em 5K e em 7K
    /// e' jogado com charts diferentes, e ate' com dificuldades diferentes onde faltou captura,
    /// por isso as pontuacoes nao se comparam. Estavam a ir todas para a mesma chave.
    /// </summary>
    public string Canal { get; init; } = Config.Canal5K;

    /// <summary>
    /// A chave com que o melhor de um course fica guardado: <c>"5k:12"</c>, <c>"7k:12"</c>.
    ///
    /// As contas anteriores a esta separacao tem o numero cru (<c>"12"</c>); o
    /// <see cref="UserStore"/> converte-as para o canal 5K ao carregar, que e' o unico que
    /// existia quando foram escritas.
    /// </summary>
    public string ChaveDoCourse(int course) => $"{Canal}:{course}";

    public ResponsiveSession(TcpClient tcp, ResponseMap map, IPAddress advertise, int id,
                             Action<string> log, SongLibrary? songs = null,
                             ResponseMap? outroMapa = null)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _map = map;
        _outroMapa = outroMapa;
        _advertise = advertise;
        _id = id;
        _log = log;
        _songs = songs;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log($"connected from {_tcp.Client.RemoteEndPoint}");
        var reader = new PacketReader(_stream, MessageSizes.ClientToServer);

        try
        {
            await _stream.WriteAsync(new byte[] { 0x03, 0x00, 0xCC }, ct);   // ServerHello

            while (!ct.IsCancellationRequested)
            {
                byte[]? packet;
                try { packet = await reader.ReadPacketAsync(ct); }
                catch (ProtocolViolationException ex) { Log($"STOP: {ex.Message}"); break; }
                if (packet is null) { Log("client closed the connection"); break; }

                ushort id = BitConverter.ToUInt16(packet, 0);

                if (id == 0x000A) { await SendConnectAckAsync(packet, ct); continue; }

                // Keepalive temporizado, independente do que o cliente esteja a enviar.
                if (_outgoing is not null && DateTime.UtcNow - _lastAlive >= AliveInterval)
                {
                    var alive = FindAnywhere(AliveReq).FirstOrDefault(m => m.Id == AliveReq);
                    if (alive.Header is not null)
                    {
                        _lastAlive = DateTime.UtcNow;
                        await SendAsync(alive, ct);
                    }
                }

                // Decifrar o que o cliente envia. O ConnectReq acima e' a excecao: vai em
                // claro porque e' anterior a' chave. A partir daqui tudo com 8 ou mais
                // bytes vem cifrado, e o estado tem de avancar por TODOS os pacotes, se
                // sejam ou nao interessantes â€” saltar um dessincroniza os seguintes.
                byte[] plain = Array.Empty<byte>();
                if (packet.Length >= PacketCodec.MinEncryptedSize && _incoming is not null)
                {
                    plain = packet.AsSpan(PacketCodec.HeaderSize).ToArray();
                    _incoming.Decrypt(plain);
                }

                // contar as ocorrencias de cada pedido: a n-esima vez recebe o que o
                // servidor original respondeu a' n-esima vez
                int n = _seen.GetValueOrDefault(id);
                _seen[id] = n + 1;

                // Nenhuma gravacao cobre tudo: a sessao de jogo nao passou pela loja, a da
                // loja nao jogou musicas. Quando a principal nunca viu este pedido — o que
                // e' diferente de o ter visto e nao ter respondido — procura-se nas
                // outras. Sem isto, o cliente pede algo que a gravacao nao tem, fica sem
                // resposta e vai parar a um estado indefinido.
                // Qual das duas gravacoes serve esta ligacao — decidido pelo primeiro pedido
                // que traga resposta, nao pela ordem da ligacao. Ver _outroMapa.
                if (!_mapaEscolhido && _outroMapa is not null && id is 0x0011 or 0x001B)
                {
                    _mapaEscolhido = true;
                    if (!_map.HasRequest(id) && _outroMapa.HasRequest(id))
                    {
                        Log($"   (0x{id:x4}: this connection is {(id == 0x0011 ? "autenticacao" : "jogo")}; " +
                            $"switching to {_outroMapa.Nome})");
                        _map = _outroMapa;
                    }
                }

                var fonte = _map;
                if (!_map.HasRequest(id))
                    foreach (var extra in Extras)
                        if (extra.HasRequest(id)) { fonte = extra; break; }

                // DIAGNOSTICO --login-de: so' a rajada de login troca de gravacao.
                if (LoginDe is not null && id == 0x001B)
                {
                    var outra = new[] { _map }.Concat(Extras).FirstOrDefault(x => x.Nome == LoginDe);
                    if (outra is not null) { fonte = outra; Log($"   (login served by {LoginDe})"); }
                    else Log($"   (WARNING: --login-de {LoginDe} is not loaded)");
                }

                var replies = fonte.For(id, fonte == _map ? n : 0);
                if (fonte != _map && replies.Count > 0)
                    Log($"   (reply came from a supplementary recording)");

                // CONTINUAR UM COURSE FALHADO. O cliente carrega em "continue", manda o
                // 0x0087 e fica a' espera do 0x0088; sem ele nao sai do ecra de game over.
                //
                // Vai a' mao porque o agrupamento por proximidade nao serve: na fail_s1 o
                // cliente manda 0x006F, 0x006A, 0x0087 e 0x00F0 todos na mesma rajada, e o
                // 0x0088 acaba atribuido ao 0x00F0. O ack nao leva carga util nenhuma —
                // corpo todo a 0xCC — por isso basta reenviar o gravado.
                if (id == ContinueCourseReq && replies.Count == 0)
                {
                    var ack = new[] { _map }.Concat(Extras)
                        .Select(f => f.PrimeiraDe(ContinueCourseAck))
                        .FirstOrDefault(m => m is not null);
                    if (ack is not null)
                    {
                        replies = new List<ResponseMap.Message> { ack.Value };
                        Log("   (continue course: 0x0088)");
                    }
                    else Log("   (WARNING: 0x0087 with no 0x0088 in any recording)");
                }

                // AS SALAS DE OUTROS JOGADORES NAO EXISTEM AQUI. O 0x003A e' uma entrada da
                // lista de salas do lobby — na end_s1 vem uma na rajada de login e era ela
                // que punha o "popskypk1 的自撸房间" e o "Gaejonmot" a aparecer como salas
                // fantasma. Este servidor nunca tem outros jogadores, por isso qualquer
                // 0x003A gravado e' mentira.
                //
                // Nao e' um remendo: e' o que o servidor real faz com o lobby vazio. A
                // vel_s1 foi gravada assim de proposito e o MESMO pedido da' respostas
                // diferentes — 0x0073 responde "0x74 0x3c 0x3a" na eq2_s2 e so' "0x74 0x3c"
                // na vel_s1; a rajada de login perde igualmente o 0x3a do fim.
                // QUEM SE ESTA' A LIGAR. As credenciais vem no AuthenticateInACCReq, e
                // agora sabem-se ler (ver Protocol/Credentials.cs): a chave e' o CRC32 dos
                // 32 bytes de sessao que o proprio pacote transporta, e a ofuscacao e'
                // `~claro + chave.byte[i%4]` seguida de uma permutacao escolhida pela
                // chave. Ate' aqui a conta tinha de ser escolhida a' mao com --user.
                if (id == Protocol.Credentials.MessageId && Store is not null &&
                    packet.Length >= Protocol.Credentials.PacketSize)
                {
                    var completo = (byte[])packet.Clone();
                    plain.CopyTo(completo, PacketCodec.HeaderSize);
                    var (utilizador, password) = Protocol.Credentials.Ler(completo);

                    if (!string.IsNullOrWhiteSpace(utilizador))
                    {
                        var conta = Store.GetOrCreate(utilizador);

                        // Conta nova: a primeira password fica a valer.
                        if (string.IsNullOrEmpty(conta.Password)) { conta.Password = password; Store.Save(); }

                        if (conta.Password != password)
                        {
                            // Recusar em condicoes. Deixar passar em silencio nao chega: o
                            // cliente fica parado no ecra de confirmacao a' espera de uma
                            // resposta que nunca chega.
                            var neg = FindAnywhere(Protocol.Credentials.AckMessageId)
                                          .FirstOrDefault(m => m.Id == Protocol.Credentials.AckMessageId);
                            if (neg.Header is not null && neg.Body is not null)
                            {
                                var cab = (byte[])neg.Header.Clone();
                                for (int k = 3; k < cab.Length; k++) cab[k] = 0;   // sem id de conta
                                var corpo = new byte[neg.Body.Length];             // tudo a zero
                                BitConverter.TryWriteBytes(
                                    corpo.AsSpan(Protocol.Credentials.AckResultOffset, 4),
                                    Protocol.Credentials.AckPasswordErrada);
                                await SendAsync(new ResponseMap.Message(neg.Id, cab, corpo), ct);
                                Log($"   (wrong password for \"{conta.Name}\": refused)");
                                continue;
                            }
                            Log($"   (WARNING: wrong password but no recorded 0x0010 to refuse with)");
                        }

                        Account = conta;
                        Profile = Store.Bind(conta);
                        Names = new Protocol.NameRewriter("MDashK", conta.NomeVisivel);
                        Log($"   (login as \"{utilizador}\" — account {conta.Name}, {Profile})");
                    }
                }

                // Loja e inventario. Na captura inv2, onde o jogador saltou entre os dois
                // ecras varias vezes de seguida, o servidor real responde SEMPRE assim:
                //
                //   C 0x012c -> S 0x012d     (loja)
                //   C 0x00f0                 (sem resposta nenhuma)
                //   C 0x00fd -> S 0x00fe     (inventario)
                //
                // O 0x00F0 nunca leva resposta enquanto se anda nestes dois ecras — e'
                // um pedido de dados de ecra que o cliente ja' tem. Responder-lhe
                // entrega o ecra' duas vezes, e era o que congelava o jogo ao saltar da
                // loja direto para o inventario. Dentro da sala continua a levar
                // resposta, dai' a distincao pelo ecra anterior.
                if (id is OpenShopReq or SystemInfoReq)
                {
                    ushort certo = id == OpenShopReq ? ShopDataInf : SystemInfoAck;
                    var gravado = FindAnywhere(certo);
                    if (gravado.Count > 0)
                    {
                        Log($"   ({(id == OpenShopReq ? "loja" : "inventario")})");
                        replies = gravado;
                    }
                    else Log($"   (WARNING: no recording of 0x{certo:x4}; the client will go to the wrong place)");
                }
                // Escolher e navegar nos courses. O contador de ocorrencias nao serve aqui:
                // na gravacao o PRIMEIRO 0x0083 nao teve resposta (foram 20 em 21), e o
                // jogador percorre a lista muitas mais vezes do que quem gravou. Sem isto o
                // cliente fica sem a resposta que espera e entra em ciclo — via-se no log
                // como centenas de 0x0085 seguidos.
                // O 0x0083 NAO leva resposta propria. Na gravacao o cliente manda-o logo
                // seguido de um 0x0085, e o servidor responde uma so' vez, depois dos dois:
                //
                //   C 0x0083 -> C 0x0085 -> S 0x0084 -> S 0x0086
                //
                // Responder a cada um mandava um 0x0086 a mais e fora de ordem. O
                // ResponseMap atribui o par ora a um ora a outro conforme a ordem em que
                // sairam, o que faz parecer que ambos tem resposta — nao tem.
                // Qual o course escolhido. O indice vai no cabecalho[3..4] tanto do
                // seleccionar como do navegar, e e' a chave da tabela do courses.txt.
                if (id is CourseSelectReq or CourseBrowseReq && packet.Length >= 5)
                    _courseIndex = BitConverter.ToUInt16(packet, 3);

                if (id == CourseSelectReq)
                {
                    // CADA 0x0083 LEVA UM 0x0084, JA'. E cada 0x0085 leva um 0x0086, e mais
                    // nada. Contado no fio, nas cinco gravacoes, sem uma unica excepcao:
                    //
                    // | gravacao | C 0x0083 | S 0x0084 | C 0x0085 | S 0x0086 |
                    // |---|---|---|---|---|
                    // | course_s1 | 21 | 21 | 16 | 16 |
                    // | course2_s1 | 1 | 1 | 1 | 1 |
                    // | course3_s1 | 14 | 14 | 21 | 21 |
                    // | fail_s1 | 4 | 4 | 7 | 7 |
                    // | full_s1 | 2 | 2 | 2 | 2 |
                    //
                    // Foi preciso contar para ver. Todas as leituras anteriores sairam do mapa
                    // de respostas, que agrupa por proximidade: quando o cliente manda os dois
                    // pedidos colados, o 0x0084 do 0x0083 cai no balde do 0x0085 e parece que
                    // e' o 0x0085 que o traz. Dai' a regra antiga — guardar a tabela e solta-la
                    // com o 0x0085 — e dai' o arrasto: a tabela do course iluminado so' saia'
                    // na mensagem seguinte, por isso o cliente mostrava sempre uma atrasada.
                    var rank = RespostaDeCourse(packet, so0x0084: true);
                    if (rank.Count == 0 && MoldeDeTabelaVazia() is { } vazia)
                    {
                        rank = new List<ResponseMap.Message> { vazia };
                        Log($"   (no 0x0084 for course {CourseDoPedido(packet)}: sending the empty template)");
                    }
                    if (rank.Count > 0)
                    {
                        _rankCourse = CourseDoPedido(packet);
                        replies = rank.Concat(replies.Where(m => m.Id != CourseRankAck)).ToList();
                    }
                }
                else if (id == CourseBrowseReq)
                {
                    // O jogador percorreu os 21 courses na captura, por isso ha' uma
                    // resposta gravada para cada um. Procura-se PELO COURSE PEDIDO, nao
                    // pela ordem em que aconteceu.
                    if (packet.Length >= 7)
                    {
                        uint qual = BitConverter.ToUInt32(packet, 3) & 0x00FFFFFFu;   // [6] e' ruido de sessao

                        // Procura-se a chave nos DOIS pedidos, nao so' no que chegou. O
                        // cliente manda o 0x0083 e o 0x0085 seguidos, e o 0x0084 fica no
                        // balde do ULTIMO dos dois — que alterna conforme a ordem em que
                        // sairam. Procurar so' no proprio id encontrava o balde vazio,
                        // caia-se no recurso, e vinha o ranking de outro course: pedia-se
                        // o "Let's Begin" e aparecia o do "Feel So Good".
                        // A resposta do par fica guardada sob a chave do pedido que chegou
                        // POR ULTIMO — e essa chave leva a accao desse pedido. Por isso nao
                        // basta trocar o id: e' preciso trocar tambem a accao. Para o mesmo
                        // course tentam-se as duas formas:
                        //     (0x0085, course | accao do 0x0085)
                        //     (0x0083, course | 0x00)          <- a accao do seleccionar
                        ushort course = (ushort)(qual & 0xFFFF);
                        var tentativas = new (ushort Pedido, uint Chave)[]
                        {
                            (id, qual),
                            (CourseSelectReq, course),                    // accao 0x00
                            (CourseBrowseReq, course | 0x200000u),        // accao 0x20
                        };

                        foreach (var fonte2 in new[] { _map }.Concat(Extras))
                        {
                            foreach (var (pedido, chave) in tentativas)
                            {
                                var certa = fonte2.ForKey(pedido, chave);
                                if (certa.Count > 0) { replies = certa; break; }
                            }
                            if (replies.Count > 0) break;
                        }
                    }

                    if (replies.Count == 0)
                        foreach (var fonte2 in new[] { _map }.Concat(Extras))
                        {
                            var alt = fonte2.FirstNonEmpty(id);
                            if (alt.Count > 0) { replies = alt; break; }
                        }

                    // A resposta TEM de identificar o course que foi pedido. O indice vai
                    // no cabecalho [3..4] tanto no pedido como na resposta — vê-se nos
                    // pares gravados: pedido `00 00` -> `0x0086 cab=..47 0000`, pedido
                    // `02 00` -> `cab=..20 0200`.
                    //
                    // Sem isto o cliente recebe sempre o ranking do mesmo course, nao
                    // reconhece a resposta ao que perguntou, e volta a perguntar — era o
                    // ciclo infinito de 0x0085 que enchia o log.
                    if (replies.Count > 0 && packet.Length >= 5)
                    {
                        var corrigidas = new List<ResponseMap.Message>(replies.Count);
                        foreach (var m in replies)
                        {
                            if (m.Header is null || m.Header.Length < 5) { corrigidas.Add(m); continue; }
                            var cab = (byte[])m.Header.Clone();
                            cab[3] = packet[3];
                            cab[4] = packet[4];
                            corrigidas.Add(new ResponseMap.Message(m.Id, cab, m.Body));
                        }
                        replies = corrigidas;
                        Log($"   (course {BitConverter.ToUInt16(packet, 3)}, action 0x{BitConverter.ToUInt16(packet, 5):x4}: {replies.Count} reply(ies))");
                    }

                    // ESPERAR DEPOIS DO 0x0084, COMO O SERVIDOR REAL.
                    //
                    // Medido nos tempos da course2_s1 contra os da minha propria captura, no
                    // mesmo ponto (entrar no course sem passar pelo free mode):
                    //
                    //   real:  C 0x0085 -> S 0x0084 (+7 ms) -> S 0x0086 (+207 ms)
                    //   meu:   C 0x0085 -> S 0x0084 (+5 ms) -> S 0x0086 (+4 ms)
                    //
                    // O 0x0084 sao 2155 bytes de ranking. Mandar o 0x0086 colado a ele deixa o
                    // cliente bloqueado quando a sala de course e' a PRIMEIRA da sessao — com
                    // uma sala de free mode criada e abandonada antes, aguenta. E' a unica
                    // diferenca que sobra: todos os bytes desta janela sao iguais aos do
                    // servidor real, cabecalhos incluidos.
                    if (_ultimo0x0084 is { } quando &&
                        DateTime.UtcNow - quando < PausaDepoisDoRanking)
                    {
                        var falta = PausaDepoisDoRanking - (DateTime.UtcNow - quando);
                        Log($"   (0x0086: waiting {falta.TotalMilliseconds:F0} ms after the 0x0084)");
                        await Task.Delay(falta, ct);
                    }

                    // O 0x0085 LEVA SO' O 0x0086. Nunca o 0x0084 — esse e' do 0x0083, que
                    // vem sempre antes. Ver a contagem la' em cima.
                    //
                    // Mandar os dois faz sair 2155 bytes de detalhe do course A DOBRAR no
                    // momento em que o course e' montado — vi-o na captura do meu proprio
                    // servidor em captures/course_fail.pcapng, lado a lado com a gravacao.
                    replies = replies.Where(m => m.Id != CourseRankAck).ToList();
                    Log($"   (0x0085 of course {CourseDoPedido(packet)}: only " +
                        $"{string.Join(" ", replies.Select(m => $"0x{m.Id:x2}"))})");
                }

                // A VELOCIDADE vem do CLIENTE, no cabecalho (que vai em claro): no 0x00C3
                // quando ele pede a musica, no 0x00C5 quando muda com o F5 a meio. Guarda-se
                // para lha devolver no 0x00C4. Ver Protocol.Velocidade.
                if ((id == Protocol.Velocidade.MessageIdCliente ||
                     id == Protocol.Velocidade.MessageIdMudanca) &&
                    Protocol.Velocidade.PodeLer(packet))
                {
                    var v = Protocol.Velocidade.Ler(packet);
                    if (_velocidade != v)
                    {
                        _velocidade = v;
                        Log($"   (speed chosen: {Protocol.Velocidade.Nome(v.Indice)}" +
                            $" — index {v.Indice}, scroll {v.Scroll})");
                    }
                }

                // O ECRA DE BOAS-VINDAS de uma conta nova. Ver Protocol.BoasVindas: o
                // nickname vem no 0x0030 e a idade e o sexo no 0x0032, os dois com os campos
                // no cabecalho, que viaja em claro.
                if (Account is not null && id == Protocol.BoasVindas.NicknameReq)
                {
                    var nick = Protocol.BoasVindas.LerNickname(packet, plain);
                    if (!string.IsNullOrWhiteSpace(nick))
                    {
                        Account.Nickname = nick;
                        Names = new Protocol.NameRewriter("MDashK", Account.NomeVisivel);
                        Store?.Save();
                        Log($"   (nickname chosen: \"{nick}\")");
                    }
                }

                if (Account is not null && id == Protocol.BoasVindas.PerfilReq &&
                    Protocol.BoasVindas.PodeLerPerfil(packet))
                {
                    var (idade, sexo) = Protocol.BoasVindas.LerPerfil(packet);
                    Account.Idade = idade;
                    Account.Sexo = sexo;
                    // O avatar de partida segue o sexo: a conta feminina da captura nasceu com
                    // o 0xF001. Se o jogador ja' tiver escolhido outro, nao se mexe.
                    if (Account.Avatar == 0)
                        Account.Avatar = sexo == 1 ? Protocol.UserInfo.AvatarFeminino
                                                   : Protocol.UserInfo.AvatarMasculino;
                    Store?.Save();
                    Log($"   (profile chosen: {idade} years old, sex {(sexo == 1 ? "feminino" : "masculino")}" +
                        $" — {Protocol.BoasVindas.Cru(packet, plain)})");
                }

                // E OS EFFECTORES, que vem no CORPO do mesmo 0x00C3 e nao no cabecalho.
                // Ver Protocol.Effectores.
                if (id == Protocol.Velocidade.MessageIdCliente &&
                    Protocol.Effectores.PodeLer(plain))
                {
                    _effectores = Protocol.Effectores.Ler(plain);

                    // A CAIXA SPEED VEM NO CABECALHO, nao no corpo — e paga bonus como um
                    // effector. Ver Protocol.ScoreFormula.BonusDaVelocidade.
                    if (Protocol.Velocidade.PodeLer(packet))
                    {
                        _modoVelocidade = Protocol.Velocidade.Ler(packet).Indice;
                        if (Protocol.ScoreFormula.NomeDaVelocidade(_modoVelocidade) is { } qual)
                            Log($"   ({qual}: +{Protocol.ScoreFormula.BonusDaVelocidade(_modoVelocidade)} bonus)");
                    }
                    if (Protocol.Effectores.Descrever(_effectores) is { Length: > 0 } quais)
                        Log($"   (effectors chosen: {quais})");
                }

                // A musica foi abaixo? Decide se o fecho espera pelo 0x0087. Ver a nota junto
                // ao envio das respostas, e Protocol.StageEndReport.
                //
                // NAO SO' EM COURSE MODE. Este teste tinha um `&& _courseRoom` que o desligava
                // no free mode, e por isso um game over de musica solta passava por jogada
                // completa: ia buscar XP ao XpDaJogada (que tem chao de 20) e 15 de MAX pela
                // media, e ate' podia subir de nivel. Ver a nota do PlayOverReq.
                if (id == Protocol.StageEndReport.MessageId &&
                    Protocol.StageEndReport.PodeLerBarra(plain))
                {
                    float barra = Protocol.StageEndReport.LerBarra(plain);
                    _etapaFalhada = barra == 0f;
                    Log($"   (end of song: gauge {barra:0.#}{(_etapaFalhada ? " — falhada" : "")})");
                }

                // Que sala se esta' a criar — o pedido e' o mesmo nos dois modos.
                if (id == CreateRoomReq)
                {
                    _tipoSala = plain.Length > RoomTypeOffset ? plain[RoomTypeOffset] : RoomTypeFree;
                    _courseRoom = _tipoSala == RoomTypeCourse;
                    _courseSong = 0;   // sala nova: a sequencia recomeca na primeira musica
                    _totalCorridoCourse = 0;
                    _breaksCourse = 0;
                    _maxGanhoCourse = 0;
                    _xpGanhoCourse = 0;
                    _notasCourse = 0;
                    _pontuacaoCourse = 0;
                    _bonusCourse = 0;
                    _precisoesCourse.Clear();
                    _courseFalhado = false;
                    _pontuacaoRanking = 0;
                    _etapaRanking = 0;
                    _itemSorteado = 0;   // o sorteio e' de cada course, nao se arrasta
                    _arranqueEmEspera = null;
                    Log($"   (room: {(_courseRoom ? "course mode" : "free mode")})");
                }

                // Dentro da sala, o 0x00F0 traz os dados do ecra. No course mode a resposta
                // inclui a lista de courses; no free mode nao. Escolhe-se pelo tipo de sala.
                // Rede de seguranca: se o 0x00F0 nao vier, a rajada sai a' mesma no pedido
                // seguinte que nao seja batimento. Mais vale arrancar fora de ordem do que
                // deixar o cliente eternamente no ecra de carregamento.
                if (_arranqueEmEspera is { Count: > 0 } atrasada &&
                    id != ScreenDataReq && id != ClientAck && id != AliveReq)
                {
                    _arranqueEmEspera = null;
                    Log($"   (WARNING: the first song's burst went out on 0x{id:x4}; the 0x00f0 never arrived)");
                    foreach (var m in atrasada) await SendAsync(m, ct);
                }

                if (id == ScreenDataReq && _lastScreen == CreateRoomReq)
                {
                    // Os TRES tipos de sala escolhem-se por conteudo, nao por ocorrencia — pelo
                    // proprio byte do modo que a rajada devolve. Ver RoomTypeOffset.
                    var sala = GrupoDeSala(_tipoSala);
                    if (sala.Count > 0)
                    {
                        // PRIMEIRA SALA DA SESSAO E E' DE COURSE: passa-se primeiro por uma
                        // sala de free mode e sai-se dela.
                        //
                        // O cliente congela ao carregar em START quando a sala de course e' a
                        // primeira da sessao. Nao e' nada do que vai no fio: comparei a minha
                        // captura contra a course2_s1 — que tambem entrou directa no course e
                        // funcionou — e o burst da sala, o 0x0082, o 0x0084 e o 0x0086 sao
                        // byte a byte iguais, cabecalhos incluidos. Os tempos tambem nao sao:
                        // na minha captura que funciona os intervalos do cliente sao iguais
                        // aos da que falha.
                        //
                        // O que resolve, e esta' testado, e' entrar numa sala de free mode e
                        // sair logo — nem e' preciso tocar nada. E' estado que o cliente so'
                        // constroi nesse percurso. Como nao ha' nada a corrigir no conteudo,
                        // faz-se-lhe o percurso: sala de free mode, saida, e so' entao a de
                        // course. O cliente ve' exatamente a sequencia que ja' se sabe que o
                        // deixa funcionar.

                        replies = _courseRoom ? ComListaDeCourses(sala) : sala;
                        Log($"   (room of {(_courseRoom ? "course" : "free mode")}: " +
                            $"{string.Join(" ", sala.Select(m => $"0x{m.Id:x2}"))})");
                    }
                    else Log($"   (WARNING: no recorded room group for {(_courseRoom ? "course" : "free mode")})");
                }
                else if (id == ScreenDataReq && _lastScreen != CreateRoomReq)
                {
                    // O 0x00F0 so' leva os dados da sala quando vem a seguir a um
                    // CreateRoomReq — e' assim em TODAS as capturas (skin_s3, eq2_s2,
                    // inv2_s1). Em qualquer outro ecra o servidor real cala-se.
                    //
                    // Antes so' o silenciava na loja e no inventario, por nome. Isso deixa
                    // de fora todos os ecras que ainda nao conheco — o das definicoes, por
                    // exemplo — e cada um deles cai nos dados da sala e atira o jogador
                    // para o free mode. Inverter a regra (responder so' na sala) cobre-os
                    // a todos sem ter de os enumerar.
                    // A PRIMEIRA musica de um course arranca AQUI, nao no StartReq.
                    //
                    // Na course2_s1 o cliente manda 0x00C3, 0x005F e 0x00F0, e so' depois dos
                    // TRES e' que o servidor solta a rajada (linhas 33-36 do timeline). Nas
                    // etapas 2 e 3 nao ha' 0x00F0 nenhum e a rajada responde mesmo ao 0x005F.
                    // Igual na course_s1.
                    //
                    // Responder ja' no 0x005F entrega a configuracao da jogada enquanto o
                    // cliente ainda esta' a pedir os dados do ecra.
                    if (_arranqueEmEspera is { Count: > 0 } emEspera)
                    {
                        _arranqueEmEspera = null;
                        Log($"   (0x00f0 of the course's first song: the burst goes out now, " +
                            $"{emEspera.Count} messages)");
                        foreach (var m in emEspera) await SendAsync(m, ct);
                        continue;
                    }

                    Log(_courseRoom
                        ? "   (0x00f0 of the course: no reply)"
                        : "   (0x00f0 outside a room: no reply, same as the real server)");
                    replies = new List<ResponseMap.Message>();
                }

                // O back na lista de musicas. O servidor real responde
                // `0x0074 LeaveRoomAck` (12 B, corpo de 5 zeros) seguido de um
                // `0x003c` e dois `0x003a` — medido em captures/equip2.pcapng.
                //
                // A conclusao anterior de que o servidor real nao respondia ao 0x0073
                // veio da back.pcapng, onde o jogador fez logout em vez de sair da sala:
                // nessa gravacao o back nunca chegou a funcionar, logo nao havia resposta
                // nenhuma para observar. Comparar duas sessoes so' prova alguma coisa se
                // pelo menos uma delas tiver feito o que se quer reproduzir.
                if (id == LeaveRoomReq)
                {
                    var sair = FindAnywhere(LeaveRoomAck);
                    if (sair.Count > 0) { replies = sair; Log("   (back: LeaveRoomAck)"); }
                    else Log("   (WARNING: no recorded 0x0074; back will not work)");
                }

                // Guardar o contexto para o 0x00F0 que vem a seguir.
                // O arranque da musica CONTA como mudanca de ecra. Sem isto o `_lastScreen`
                // fica preso em CreateRoomReq e o 0x00F0 que o cliente manda enquanto carrega
                // a musica volta a receber os dados da sala — por cima do carregamento. O
                // cliente fecha-se no ecra de loading, no free mode e no course.
                //
                // O servidor real nao manda nada nesse ponto: na gravacao o 0x00F0 aparece
                // ANTES da rajada de arranque e e' a rajada que lhe responde, nao havendo
                // segundo 0x00F0 nenhum.
                if (id is OpenShopReq or SystemInfoReq or CreateRoomReq
                       or Protocol.RequestId.StartReq or StartPressed) _lastScreen = id;

                // O AliveReq e' um keepalive que o servidor manda por sua iniciativa, de
                // ~50 em 50 segundos; o AliveAck do cliente e' so' a confirmacao e nao
                // leva resposta. Na gravacao alguns AliveReq calharam a seguir a um
                // AliveAck e ficaram nesse balde — reenvia-los ai' cria um pingue-pongue:
                // o cliente confirma, o servidor volta a perguntar, e assim
                // indefinidamente. Tira-se das respostas e passa a ser temporizado.
                if (replies.Any(m => m.Id == AliveReq))
                    replies = replies.Where(m => m.Id != AliveReq).ToList();

                // Confirmacoes e batimentos: so' passa o que consta da lista branca.
                if (RespostasLegitimas.TryGetValue(id, out var permitidas) && replies.Count > 0)
                {
                    var indevidas = replies.Where(m => !permitidas.Contains(m.Id)).ToList();
                    if (indevidas.Count > 0)
                    {
                        Log($"   (0x{id:x4} is an ack/heartbeat: discarded " +
                            $"{string.Join(" ", indevidas.Select(m => $"0x{m.Id:x2}"))})");
                        replies = replies.Where(m => permitidas.Contains(m.Id)).ToList();
                    }
                }

                // O AVISO DO ITEM DE BOAS-VINDAS, na faixa vermelha do topo.
                //
                // A conta nova recebe o item de boas-vindas, mas recebia-o em silencio: o
                // servidor real anuncia-o com um 0x0039 do tipo 0x02 na propria sessao do
                // login, e a nossa rajada nao o tinha. A gravacao que serve o login foi tirada
                // de uma conta ja' feita, que nao ganha item nenhum — por isso o 0x0039 que la'
                // esta' e' so' o 0x03 do canal, e esse ja' vai no lobby.
                //
                // Vai pelo mesmo criterio do ecra de boas-vindas: conta sem nickname e' conta
                // que acabou de nascer. Ver Protocol.Aviso.
                if (id == Protocol.Credentials.MessageId && Account is not null &&
                    string.IsNullOrWhiteSpace(Account.Nickname) &&
                    !replies.Any(m => Protocol.Aviso.EDoSistema(m.Body)) &&
                    AvisoDoSistema() is { } aviso)
                {
                    replies = replies.Append(aviso).ToList();
                    Log("   (welcome item notice appended to the login burst)");
                }

                // O PlaySkipInf e' a confirmacao de um salto pedido pelo jogador, e nada
                // mais. Na gravacao alguns calharam logo a seguir a um relatorio de
                // estado e ficaram nesse balde; reenvia-los ai' manda o cliente sair da
                // musica sem ninguem ter pedido â€” a musica acabava sozinha a meio.
                if (id == PlayStateInf)
                {
                    // As respostas ja' foram filtradas pela lista branca acima. Aqui so'
                    // fica a leitura do estado da jogada.

                    // Contar os breaks pelo HP. Cada um custa 6.00; varios seguidos
                    // fundem-se numa so' queda, por isso conta-se a GRANDEZA da queda e
                    // nao o numero de quedas.
                    if (Protocol.PlayState.CanReadHp(plain))
                    {
                        float hp = Protocol.PlayState.ReadHp(plain);
                        if (_hp is null) _hp = hp;                 // primeiro valor: e' o inicial
                        else if (hp < _hp - 0.01f)
                        {
                            int quantos = Protocol.PlayState.BreaksNaQueda(_hp.Value - hp);
                            _breaks += quantos;
                            Log($"   ({quantos} break(s): client gauge {_hp:F2} -> {hp:F2}; {_breaks} in this song)");
                        }
                        _hp = hp;
                    }

                    // A precisao acumulada vem no mesmo pacote; fica sempre a ultima.
                    if (Protocol.PlayState.CanReadAccuracy(plain))
                    {
                        _precisao = Protocol.PlayState.ReadAccuracy(plain);
                    }
                }

                // E o inverso: sem a confirmacao, o ESC nao faz nada. Se a ocorrencia
                // atual nao a trouxer, vai buscar-se a que a gravacao tenha.
                if (id == PlaySkipReq && !replies.Any(m => m.Id == PlaySkipInf))
                {
                    var skip = _map.FirstNonEmpty(PlaySkipReq).Where(m => m.Id == PlaySkipInf).ToList();
                    if (skip.Count > 0) { replies = skip; Log("   (skip confirmation recovered)"); }
                }

                // Arranque da musica: trocar SO' o GameInfoInf pelo da musica escolhida.
                //
                // Comparadas as cinco musicas colhidas, as outras cinco mensagens do
                // grupo tem o corpo IGUAL em todas â€” nao trazem nada da musica. O que
                // varia nelas e' o cabecalho do 0x00C4, e isso parece estado da sessao
                // onde foram gravadas, nao da musica. Servir o grupo inteiro injecta esse
                // estado numa sessao que nunca o viveu, e o cliente crasha depois do
                // loading. Fica so' a mensagem que e' mesmo especifica da musica.
                // Arranque da musica.
                //
                // Usa-se SEMPRE a primeira ocorrencia gravada do StartReq para as
                // mensagens pequenas, e nao a que corresponderia a esta contagem.
                //
                // O cabecalho do 0x00C4 traz estado acumulado da sessao onde foi gravado
                // (00/07 no primeiro arranque, 02/09 no segundo). O cliente valida-o: dar
                // os valores do segundo arranque a quem acabou de entrar deixa-o
                // bloqueado. Como este servidor nao acompanha esse estado, o mais seguro
                // e' que todo o arranque se pareca com o primeiro â€” foi a unica
                // combinacao que se observou a funcionar de ponta a ponta.
                if (id == Protocol.RequestId.StartReq)
                {
                    // Comeca uma musica nova: qualquer fecho pendente da anterior deixa
                    // de fazer sentido. Sem isto, um fim de musica que nao chegou a ser
                    // entregue dispara ja' dentro da jogada seguinte.
                    if (_stageFinished) { _stageFinished = false; Log("   (pending closing discarded)"); }

                    // E A MARCA DE FALHADA TAMBEM SE LIMPA AQUI. Antes so' se limpava no fecho
                    // do "continuar course" (0x0087), que e' o unico sitio por onde uma etapa
                    // falhada passava. Agora que o free mode tambem a poe, uma musica perdida
                    // deixava a marca colada a' sessao e a seguinte — e todas as outras — nao
                    // davam XP nenhum. Trocar um erro pelo seu contrario nao e' corrigi-lo.
                    _etapaFalhada = false;

                    // Em course mode NAO ha' ChangeDiscReq: e' o servidor que decide a
                    // musica seguinte, por isso `_selected` fica vazio e a biblioteca nao
                    // ajuda. Reproduz-se o grupo gravado pela ordem em que aconteceu, que
                    // e' a ordem das musicas do course.
                    if (_courseRoom)
                    {
                        // A gravacao das musicas do course e' indicada por nome, e nao
                        // descoberta por conteudo.
                        //
                        // Descobri-la dava errado de duas maneiras ao mesmo tempo. A
                        // `full_s1` tem um course E uma musica de free mode gravados na
                        // mesma sessao, por isso "todos os grupos com GameInfoInf" sao
                        // QUATRO: as tres do course mais a do free mode. O course seguia
                        // para dentro dela e o jogador levava com uma quarta musica sem som,
                        // que nunca fez parte de course nenhum.
                        //
                        // Nao ha' sinal fiavel no mapa para separar as duas coisas: o
                        // ResponseMap guarda baldes, nao sabe em que sala cada um foi
                        // gravado. Uma gravacao so' de course resolve-o sem ambiguidade, e
                        // dizer qual e' por nome e' mais honesto do que adivinhar.
                        var doCourse = new[] { _map }.Concat(Extras)
                            .FirstOrDefault(f => f.Nome == Config.GravacaoCourse)
                            ?? new[] { _map }.Concat(Extras)
                                .FirstOrDefault(f => f.FindSetContaining(CourseListInf).Count > 0);

                        // Nao se usa o balde do pedido: na primeira musica o cliente manda
                        // um 0x00F0 entre o StartReq e a resposta, e o grupo de arranque
                        // acaba no balde desse 0x00F0 em vez do 0x005F. Procura-se pelo
                        // conteudo — os grupos que trazem o GameInfoInf — pela ordem da
                        // gravacao, que e' a ordem das musicas do course.
                        var grupos = doCourse?.AllSetsContaining(GameInfoInf)
                                     ?? new List<IReadOnlyList<ResponseMap.Message>>();
                        // Um course pode ter mais musicas do que as tres gravadas. Nesse caso
                        // repete-se a ultima rajada gravada: os corpos sao iguais entre etapas
                        // e os cabecalhos, mesmo nao sendo os proprios, sao de uma etapa de
                        // course a serio — que e' exatamente o que a biblioteca nao tem. Sem
                        // isto, da quarta musica em diante nao saia rajada nenhuma.
                        int qualGrupo = grupos.Count > 0 ? Math.Min(_courseSong, grupos.Count - 1) : -1;
                        var grupo = qualGrupo >= 0
                                    ? grupos[qualGrupo].ToList()
                                    : new List<ResponseMap.Message>();
                        if (qualGrupo >= 0 && qualGrupo != _courseSong)
                            Log($"   (stage {_courseSong + 1}: the recording only has {grupos.Count} " +
                                $"bursts; repeating {qualGrupo + 1})");
                        // Se o courses.txt souber deste course e a biblioteca tiver a musica,
                        // troca-se o bloco gravado pelo da musica certa. E' o que permite
                        // jogar courses que nunca foram capturados: as mensagens pequenas do
                        // arranque vem do grupo gravado (sao iguais em todos os courses) e so'
                        // o bloco muda.
                        // EXPERIENCIA (--course-gravado): servir a rajada GRAVADA em vez da
                        // da biblioteca.
                        //
                        // A course2_s1 e' o "Let's Begin" — as mesmas tres musicas, 126, 123
                        // e 55. Servida tal e qual, a rajada fica byte a byte igual a' do
                        // servidor real, com os cabecalhos do 0x0061, 0x00C4, 0x00C9, 0x00CA
                        // e 0x0060 que a biblioteca nao pode ter porque foi colhida em free
                        // mode. Se o course continuar a falhar assim, o erro NAO esta' na
                        // rajada de arranque.
                        //
                        // So' vale para o Let's Begin: noutro course toca as musicas gravadas.
                        var doTabela = CourseGravado ? null : Courses?.Get(_courseIndex);
                        if (CourseGravado)
                            Log($"   (--course-gravado: stage {_courseSong + 1} exactly as " +
                                $"recorded, without going through the library)");

                        if (grupo.Count > 0 && doTabela is { } curso && _songs is not null &&
                            _courseSong < curso.Musicas.Count)
                        {
                            var chave = curso.Musicas[_courseSong];

                            // O course pode pedir uma dificuldade que a biblioteca deste canal
                            // nao tem. Desce-se ate' encontrar, em vez de servir a musica
                            // gravada — ver SongLibrary.GetComRecuo.
                            var comRecuo = _songs.GetComRecuo(chave);
                            if (comRecuo is { } achado && achado.Usada != chave)
                            {
                                Log($"   (FALLBACK: {chave} is not in the library; " +
                                    $"sending {achado.Usada.DifficultyName} of the same song)");
                                chave = achado.Usada;
                            }

                            // SO' O BLOCO VEM DA BIBLIOTECA. As outras cinco mensagens da
                            // rajada ficam as da GRAVACAO DO COURSE.
                            //
                            // Isto foi medido, nao inferido. Com --course-gravado, que serve
                            // a rajada inteira tal e qual, o "Let's Begin" corre de ponta a
                            // ponta: HP a descer nas tres etapas, resultados certos,
                            // transicoes certas. Com o grupo TODO da biblioteca — colhida em
                            // FREE MODE — a primeira etapa fica com o medidor de HP inerte e
                            // o fim da segunda anuncia a etapa errada.
                            //
                            // A diferenca esta' nas cinco mensagens pequenas, e nos
                            // CABECALHOS delas: os corpos sao iguais entre etapas (0x00C4 a
                            // zeros, 0x00C9 tudo 0xFF em course, 0x0061 e 0x0060 a zeros), e
                            // os cabecalhos a biblioteca nao os pode ter porque nunca esteve
                            // num course. Ver Tools/CourseInfo.ArranqueVsBiblioteca.
                            //
                            // Ja' tinha tentado esta troca uma vez e o Let's Begin passou a
                            // rebentar no fim da primeira musica — mas isso foi antes da data
                            // do servidor, do 0x0085 e do resto; a medicao de agora diz o
                            // contrario e e' direta.
                            //
                            // O bloco leva o cabecalho DA BIBLIOTECA: e' la' que vai o
                            // comprimento total, e o da gravacao anuncia outro tamanho.
                            var blocoLib = _songs.Get(chave)?.FirstOrDefault(e => e.Id == GameInfoInf);
                            if (blocoLib is { Body.Length: > 0 })
                            {
                                // O PREFIXO DO BLOCO tem de dizer que isto e' um course e em
                                // que etapa. A biblioteca veio de free mode e traz os dois
                                // bytes a zero. Ver Protocol.GameInfoFraming.CourseFlagOffset.
                                var corpo = (byte[])blocoLib.Value.Body.Clone();
                                if (GameInfoFraming.PodeMarcarCourse(corpo))
                                {
                                    var gravado = grupo.FirstOrDefault(e => e.Id == GameInfoInf);

                                    // MESMA MUSICA QUE A GRAVACAO: copia-se o prefixo inteiro.
                                    //
                                    // Dos 9776 bytes do bloco da musica 126 so' DEZ diferem
                                    // entre o gravado e o da biblioteca, e todos no prefixo —
                                    // os dados da chart sao identicos. Servindo o prefixo
                                    // gravado, a etapa fica byte a byte igual a' do servidor
                                    // real, que e' o que o --course-gravado provou funcionar.
                                    //
                                    // Alem do +2 e do +10, ha' bytes em +70..73, +116..117 e
                                    // +122..123 numa zona que parece conteudo codificado: as
                                    // posicoes que diferem mudam de etapa para etapa, por isso
                                    // nao ha' campo para ler — mas ha' para copiar.
                                    //
                                    // MAS SO' SE FOR MESMO O MESMO CHART. A gravacao de course
                                    // e' de 5 teclas (Config.GravacaoCourse); num course de 7
                                    // teclas o bloco da biblioteca e' outra chart da mesma
                                    // musica — medido: 113 dos primeiros 128 bytes diferem, e
                                    // ate' o comprimento e' outro (musica 126 EASY: 9776 bytes
                                    // em 5K, 9782 em 7K). Copiar-lhe o prefixo de 5K por cima
                                    // punha o cliente a ler a chart com os campos errados e as
                                    // notas apareciam VAZIAS — que foi o que aconteceu no
                                    // "Let's Begin" em 7K, e nao no "Classic Land -1-" so'
                                    // porque ai' a gravacao nem sequer e' da mesma musica.
                                    //
                                    // O comprimento e' a prova mais barata de que sao a mesma
                                    // chart: no mesmo canal so' mudam dez bytes do prefixo.
                                    bool mesmaMusica = gravado.Body is { Length: >= 8 } &&
                                                       GameInfoFraming.ReadSongId(gravado.Body) == chave.Song &&
                                                       gravado.Body.Length == corpo.Length;

                                    if (mesmaMusica && gravado.Body!.Length >= GameInfoFraming.PrefixLength &&
                                        corpo.Length >= GameInfoFraming.PrefixLength)
                                    {
                                        GameInfoFraming.CopiarPrefixoGravado(corpo, gravado.Body);
                                        Log("   (block: prefix from the recording, which is the same chart)");
                                    }
                                    else if (gravado.Body is { Length: >= 8 } &&
                                             GameInfoFraming.ReadSongId(gravado.Body) == chave.Song)
                                        Log($"   (block: prefix NOT copied — the recording has " +
                                            $"{gravado.Body.Length} bytes and the library {corpo.Length}; " +
                                            "it is the same song on another channel)");

                                    // O contador de jogada (+2) vem do bloco GRAVADO desta
                                    // etapa: e' estado da sessao, nao dado da musica.
                                    byte? seq = gravado.Body is { Length: > GameInfoFraming.SequenciaOffset }
                                        ? gravado.Body[GameInfoFraming.SequenciaOffset] : null;

                                    GameInfoFraming.MarcarCourse(corpo, _courseIndex, _courseSong, seq);
                                    Log($"   (block: marked as course, stage {_courseSong} " +
                                        $"— +{GameInfoFraming.CourseFlagOffset}=1, " +
                                        $"+{GameInfoFraming.CourseIdOffset}={_courseIndex}, " +
                                        $"+{GameInfoFraming.CourseStageOffset}={_courseSong})");
                                }

                                // O CABECALHO tambem e' o gravado, com o comprimento da
                                // biblioteca. O byte [2] e' um contador de mensagem da sessao
                                // (0x37 no gravado, 0x7C na biblioteca) e os [3..6] sao o
                                // comprimento total, que tem de ser o do bloco que vai sair.
                                grupo = grupo
                                    .Select(m =>
                                    {
                                        if (m.Id != GameInfoInf) return m;
                                        var cab = (byte[])m.Header.Clone();
                                        var cabLib = blocoLib.Value.Header;
                                        if (cab.Length >= 7 && cabLib.Length >= 7)
                                            Array.Copy(cabLib, 3, cab, 3, 4);
                                        return new ResponseMap.Message(m.Id, cab, corpo);
                                    })
                                    .ToList();
                                Log($"   (course \"{curso.Nome}\", stage {_courseSong + 1}/{curso.Musicas.Count}: " +
                                    $"{Catalogo?.Nome(chave.Song) ?? ""} — {chave}, library block " +
                                    $"in the recorded burst)");
                                _musicaEmCurso = chave.Song;
                            }
                            else Log($"   (WARNING: {chave} is not in the library; sending the recorded song)");
                        }
                        else if (grupo.Count > 0 && _courseIndex >= 0 && Courses is { Count: > 0 } && doTabela is null)
                            Log($"   (course {_courseIndex} is not in courses.txt; sending the recorded song)");

                        if (grupo.Count > 0)
                        {
                            // Musica nova do course: recomecar a contagem. No free mode isto
                            // acontece no ChangeDiscReq, que em course mode nao existe — as
                            // musicas sao fixas e nao ha' escolha. Sem isto os breaks da
                            // primeira musica somavam-se aos da segunda e o ecra de cada
                            // musica saia inflacionado.
                            //
                            // Os ecras POR MUSICA mostram os valores daquela musica: nos
                            // prints do course oficial a primeira deu BREAK 0 e a segunda
                            // BREAK 2. E' o Total Result do fim que soma (BREAK 2 no total),
                            // e esse ecra ainda nao esta' implementado.
                            _hp = null; _breaks = 0; _precisao = 0; _lastReport = null;

                            Log($"   (course: song {_courseSong + 1} of {grupos.Count}, " +
                                $"{grupo.Count} messages: {string.Join(" ", grupo.Select(m => $"0x{m.Id:x2}"))})");
                            // CONTINUAR NAO AVANCA DE ETAPA: repete-se a que foi abaixo.
                            // Na fail_s1 os dois blocos das duas tentativas sao a MESMA
                            // musica (a 10) e sao byte a byte iguais tirando a velocidade.
                            //
                            // E a repetida nao e' "primeira" para efeitos de espera: o
                            // servidor real manda a rajada logo no 0x005F (192,56s -> 0x005F,
                            // 192,77s <- 0x007A), porque o 0x00F0 ja' passou na rajada do
                            // fim da etapa anterior. Trata-la como primeira punha-a a'
                            // espera de um 0x00F0 que nao volta a vir.
                            bool primeira = _courseSong == 0 && !_repetirEtapa;
                            _repetirEtapa = false;

                            // ENTRAR NUM COURSE CUSTA MAX, tal como continuar. O preco e' o
                            // `MaxPrice` do CourseSection.ini, ja' no courses.txt como
                            // `preco=`, e ate' aqui so' era cobrado no `continue` (0x0087) —
                            // a primeira tentativa saia de graca.
                            //
                            // Cobra-se na PRIMEIRA musica e nao a' escolha do course: o ecra
                            // de escolha percorre-se a's setas, e o 0x0083 chega uma vez por
                            // cada course visitado. Aqui ha' a garantia de que o course
                            // arrancou mesmo.
                            if (primeira && _profile is not null &&
                                Courses?.Get(_courseIndex) is { Preco: > 0 } cursoEntrada)
                            {
                                int antes = _profile.Max;
                                _profile.Max = Math.Max(0, _profile.Max - cursoEntrada.Preco);
                                _profile.Save();
                                Log($"   (entering course \"{cursoEntrada.Nome}\": " +
                                    $"-{cursoEntrada.Preco} MAX, {antes} -> {_profile.Max})");
                            }

                            _courseSong++;

                            // Na PRIMEIRA musica a rajada espera pelo 0x00F0. Ver a nota no
                            // sitio onde ela e' solta.
                            if (primeira)
                            {
                                _arranqueEmEspera = grupo;
                                Log("   (first song: the burst waits for the client's 0x00f0)");
                                continue;
                            }
                            foreach (var m in grupo) await SendAsync(m, ct);
                            continue;
                        }
                        Log($"   (WARNING: no start group for song {_courseSong + 1} " +
                            $"— the course recording only has {grupos.Count} group(s))");
                    }

                    // Arranque de FREE MODE. Nao pode vir de um balde de course: o 0x00C4
                    // leva estado da sala onde foi gravado e o cliente valida-o.
                    //
                    // Com a `full_s1` como principal isto deixou de ser teorico — ela grava
                    // o course ANTES da musica de free mode, por isso `For(0x005F, 0)` da' a
                    // primeira musica do course. O cliente bloqueava ao carregar em START no
                    // free mode.
                    //
                    // Primeira ocorrencia gravada do StartReq na gravacao principal. E' o
                    // comportamento com que o free mode funcionou durante todo o
                    // desenvolvimento; as variantes que tentei (ir buscar a rajada a outra
                    // gravacao, ou escolher o ultimo grupo) foram ambas piores.
                    //
                    // Pressupoe que a gravacao principal seja de FREE MODE. Com uma gravacao
                    // que tenha um course gravado antes, esta primeira ocorrencia e' de uma
                    // musica do course — ver a nota do Config.Jogo.
                    replies = _map.For(id, 0);

                    if (_selected is Protocol.SongKey chosen)
                    {
                        var group = _songs?.Get(chosen);
                        if (group is null && _songs is not null)
                        {
                            // Sem este chart, mas talvez com outra dificuldade da mesma
                            // musica. Vale mais tocar a musica certa na dificuldade
                            // errada do que a musica errada.
                            var fallback = _songs.DifficultiesFor(chosen.Song).ToList();
                            if (fallback.Count > 0)
                            {
                                var alt = new Protocol.SongKey(chosen.Song, fallback[0]);
                                group = _songs.Get(alt);
                                Log($"   (WARNING: {chosen} not harvested; using {alt})");
                            }
                        }
                        var stored = group?.FirstOrDefault(e => e.Id == GameInfoInf);
                        if (stored is { Body.Length: > 0 })
                        {
                            // Enviar o cabecalho DA MESMA gravacao que o corpo.
                            //
                            // O cabecalho do GameInfoInf traz o comprimento total do
                            // pacote nos bytes [3..6] â€” 12993 para a musica 0, 18331 para
                            // a 1, e assim por diante. Reaproveitar o cabecalho do
                            // template com o corpo de outra musica anuncia um comprimento
                            // que nao corresponde, e o cliente encrava antes sequer de
                            // carregar. Foi por isso que so' a musica 0 arrancava: era a
                            // unica em que o template ja' era o dela.
                            // NAO se corrige o byte 2 do bloco. Ele difere mesmo do que o
                            // servidor real manda (medido com libverify: 12 de 13 blocos,
                            // sempre e so' no offset 2), mas NAO e' a causa de crash nenhum:
                            // os blocos da biblioteca sao os mesmos independentemente da
                            // gravacao principal, e o free mode funcionou durante muito tempo
                            // com eles. Fica anotado como diferenca por explicar.
                            Log($"   (start #{n + 1}: song {chosen}, " +
                                $"{stored.Value.Body.Length} bytes of block + its own header)");
                            foreach (var m in replies)
                                await SendAsync(m.Id == GameInfoInf
                                    ? new ResponseMap.Message(m.Id, stored.Value.Header, stored.Value.Body)
                                    : m, ct);
                            continue;
                        }
                        Log($"   (WARNING: song {chosen} is not in the library; the wrong one will play)");
                    }
                }

                // Fim de musica. O cliente anuncia o resultado com StageResultInf e fica
                // a' espera do fecho do servidor (resultado, inventario, ReadyInf) para
                // voltar a' lista. Na gravacao esse fecho calhou na sexta vez que o
                // cliente enviou 0x0072; uma sessao com outro ritmo nunca la' chegaria e
                // ficaria presa no ecra de gameover. O gatilho certo e' o significado:
                // vindo um StageResultInf, o proximo 0x0072 leva o fecho.
                // A musica escolhida vai em claro no cabecalho do ChangeDiscReq (bytes
                // 3..6). O servidor tem de a devolver na configuracao do jogo â€” enviar
                // sempre a da gravacao faz o cliente desenhar as notas da musica que o
                // jogador escolheu mas tentar carregar os recursos de outra, que e' o
                // que deixa o jogo sem som nem video.
                // ---- Loja e inventario ----
                //
                // O cliente conhece o catalogo e os precos, e desconta o MAX sozinho; ao
                // servidor cabe registar o que a conta tem e devolver a lista.
                if (Account is not null && id == Protocol.RequestId.PurchaseItemReq && packet.Length >= 7)
                {
                    // O primeiro item vai no cabecalho[3..6]. Numa compra de carrinho
                    // ("全部购买") o cliente leva varios de uma vez, e os restantes so' podem
                    // estar no corpo: numa compra unica ele e' `00000000` seguido de 24
                    // bytes a 0xFF, ou seja seis lugares por usar.
                    //
                    // CONFIRMADO em teste: sao tres lugares de 8 bytes a partir de +4,
                    // `[catalogo:u32][0:u32]` cada, o que da' o maximo de 4 itens por
                    // compra que o carrinho do cliente permite:
                    //
                    //   2 itens  00000000 | 30840100 00000000 | FFFFFFFF...
                    //   4 itens  00000000 | 30840100 00000000 | 27880100 00000000 | 29880100 00000000
                    //
                    // A leitura e' por CONTEUDO em vez de por offset fixo: aguenta um lugar a
                    // mais sem partir e nunca toma por item um campo que o nao seja.
                    //
                    // O teste era uma gama de catalogo, 90000 a 130000, tirada dos avatares
                    // que a conta tinha na altura. Deixava de fora tres dos quatro itens que
                    // ela tem hoje — o avatar 35846, a skin 42010 e a nota 43038 — e uma
                    // compra de CARRINHO com qualquer um deles perdia-o em silencio. Agora
                    // pergunta-se a' tabela do cliente se o numero e' mesmo um item; ela tem
                    // os 269 que dao bonus, de 1025 a 64514. Ver Net.ItemTable.
                    var comprar = new List<uint> { BitConverter.ToUInt32(packet, 3) };
                    for (int p = 0; p + 4 <= plain.Length; p += 4)
                    {
                        uint v = BitConverter.ToUInt32(plain, p);
                        bool eItem = Itens?.Get(v) is not null || (v >= CatalogoMin && v <= CatalogoMax);
                        if (eItem && !comprar.Contains(v)) comprar.Add(v);
                    }
                    if (plain.Length > 0)
                        Log($"   (purchase request: body={Convert.ToHexString(plain)})");

                    foreach (var catalogo in comprar)
                    {
                        var inst = Protocol.InventoryCodec.NewInstanceId(Account.Items.Select(i => i.InstanceId));
                        Account.Items.Add(new UserStore.Item { CatalogId = catalogo, InstanceId = inst });
                        Log($"   (purchase: item {catalogo}, instance {inst})");
                    }
                    Store?.Save();
                    Log($"   ({comprar.Count} item(s) bought; {Account.Items.Count} on the account)");

                    var ack = FindAnywhere(Protocol.RequestId.PurchaseItemAck)
                                  .FirstOrDefault(m => m.Id == Protocol.RequestId.PurchaseItemAck);
                    if (ack.Body is not null && ack.Header is not null && ack.Header.Length >= 7)
                    {
                        var corpo = (byte[])ack.Body.Clone();
                        Protocol.InventoryCodec.WriteItems(corpo, Protocol.InventoryCodec.PurchaseAckListOffset,
                            Account.Items.Select(i => (i.CatalogId, i.InstanceId)), corpo.Length);

                        // O MAX que fica depois da compra viaja no CABECALHO, u16 em
                        // [5..6] — na gravacao valia 0x1707 = 5895, e nao esta' em lado
                        // nenhum do corpo. Reenviar o cabecalho gravado fazia o saldo do
                        // jogador saltar para o de quem gravou a captura assim que
                        // comprasse alguma coisa.
                        //
                        // O preco nao e' descontado: quem conhece o catalogo e os precos e'
                        // o cliente, o servidor nunca os ve'. Devolve-se o saldo da conta
                        // inalterado.
                        var cab = (byte[])ack.Header.Clone();
                        BitConverter.TryWriteBytes(
                            cab.AsSpan(Protocol.InventoryCodec.AckBalanceHeaderOffset, 2),
                            (ushort)Math.Clamp(_profile?.Max ?? Account.Max, 0, ushort.MaxValue));

                        await SendAsync(new ResponseMap.Message(ack.Id, cab, corpo), ct);
                        await ReporSaldoAsync(ct);
                        continue;
                    }
                    Log("   (WARNING: no recorded PurchaseItemAck)");
                }

                // `UseItemReq` (0x00BA). RESPONDE-SE, MAS NAO SE FAZ MAIS NADA.
                //
                // Cheguei a por aqui o efeito dos boosters, a supor que "One use" na loja
                // queria dizer que se gastavam por aqui. **Nao e' assim**: no jogo o botao do
                // MAX Booster e' o 装備 e o inventario mostra-o com 装備中, e uma sessao inteira
                // de free mode e course nao produziu um unico `0x00BA`. Os boosters sao itens
                // EQUIPADOS como qualquer outro, e o bonus deles esta' no ItemTable.Bonus.
                //
                // O que ficou aqui e' so' o ack. Nao se aplica bonus (contaria duas vezes por
                // cima do equipado) e nao se consome nada — a versao anterior fazia um
                // RemoveAll do catalogo, que apagaria TODOS os exemplares de uma vez.
                //
                // Se algum dia isto disparar, o registo diz que catalogo veio e aprende-se
                // para que serve. Ver Protocol.RequestId.UseItemReq.
                if (Account is not null && id == Protocol.RequestId.UseItemReq && packet.Length >= 7)
                {
                    uint catalogo = BitConverter.ToUInt32(packet, 3);
                    Log($"   (0x00ba UseItemReq: catalogue {catalogo} " +
                        $"({Itens?.Get(catalogo)?.Nome ?? "desconhecido"}); ack and nothing else)");

                    // Ack pelado: 3 bytes, so' cabecalho. O byte de chave devolve-se como veio.
                    await SendAsync(new ResponseMap.Message(
                        Protocol.RequestId.UseItemAck,
                        new byte[] { (byte)Protocol.RequestId.UseItemAck, 0x00, packet[2] },
                        Array.Empty<byte>()), ct);
                    continue;
                }

                // Vender. O catalogo vai no cabecalho[3..6] e a instancia no corpo[0..3];
                // a resposta e' um `SellItemAck` (0x00E0, 249 B) com a lista que sobra em
                // +2 — note-se que NAO e' o mesmo offset do PurchaseItemAck, que a tem
                // em +6.
                //
                // O valor da venda nao e' descontado nem somado: o preco esta' no cliente
                // (a nota que valia 1500 vendeu-se por 375), o servidor nunca o ve'. Aqui
                // devolve-se o saldo da conta como esta'.
                if (Account is not null && id == Protocol.RequestId.SellItemReq &&
                    packet.Length >= 7 && plain.Length >= 4)
                {
                    uint instancia = BitConverter.ToUInt32(plain, 0);
                    uint catalogo = BitConverter.ToUInt32(packet, 3);
                    int fora = Account.Items.RemoveAll(
                        i => i.InstanceId == instancia && i.CatalogId == catalogo);
                    Store?.Save();
                    Log($"   (sell: catalogue {catalogo}, instance {instancia}; " +
                        $"{fora} removed, {Account.Items.Count})");

                    var vAck = FindAnywhere(Protocol.RequestId.SellItemAck)
                                   .FirstOrDefault(m => m.Id == Protocol.RequestId.SellItemAck);
                    if (vAck.Body is not null && vAck.Header is not null && vAck.Header.Length >= 7)
                    {
                        var corpo = (byte[])vAck.Body.Clone();
                        Protocol.InventoryCodec.WriteItems(corpo,
                            Protocol.InventoryCodec.SellAckListOffset,
                            Account.Items.Select(i => (i.CatalogId, i.InstanceId)), corpo.Length);
                        var cab = (byte[])vAck.Header.Clone();
                        BitConverter.TryWriteBytes(
                            cab.AsSpan(Protocol.InventoryCodec.AckBalanceHeaderOffset, 2),
                            (ushort)Math.Clamp(_profile?.Max ?? Account.Max, 0, ushort.MaxValue));
                        await SendAsync(new ResponseMap.Message(vAck.Id, cab, corpo), ct);
                        await ReporSaldoAsync(ct);
                        continue;
                    }
                    Log("   (WARNING: no recorded SellItemAck)");
                }

                // Apagar. E' o irmao do vender e tem a mesma forma — catalogo no
                // cabecalho[3..6], instancia no corpo[0..3] — mas a resposta e' o
                // `DeleteItemAck` (0x00DC, 245 B) com a lista em +6, nao em +2.
                //
                // NAO HA' SALDO A MEXER: apagar nao devolve MAX. Por isso, ao contrario do
                // vender, nao se toca no cabecalho nem se repoe o saldo.
                //
                // O par (catalogo, instancia) e' que identifica o item. Ver DeleteItemReq:
                // no inventario que serviu de medida ha' dois itens com a MESMA instancia e
                // catalogos diferentes, e apagar so' pela instancia levava os dois.
                if (Account is not null && id == Protocol.RequestId.DeleteItemReq &&
                    packet.Length >= 7 && plain.Length >= 4)
                {
                    uint instancia = BitConverter.ToUInt32(plain, 0);
                    uint catalogo = BitConverter.ToUInt32(packet, 3);

                    // A LISTA QUE VAI NA RESPOSTA E' A DE ANTES. Guarda-se agora, porque o
                    // apagado tem de la' ir marcado — ver InventoryCodec.WriteDeleteAck.
                    var antesDeApagar = Account.Items
                        .Select(i => (i.CatalogId, i.InstanceId)).ToList();

                    int fora = Account.Items.RemoveAll(
                        i => i.InstanceId == instancia && i.CatalogId == catalogo);
                    Store?.Save();
                    Log($"   (delete: catalogue {catalogo}, instance {instancia}; " +
                        $"{fora} removed, {Account.Items.Count})");

                    var dAck = FindAnywhere(Protocol.RequestId.DeleteItemAck)
                                   .FirstOrDefault(m => m.Id == Protocol.RequestId.DeleteItemAck);
                    if (dAck.Body is not null && dAck.Header is not null)
                    {
                        var corpo = (byte[])dAck.Body.Clone();
                        var cabD = (byte[])dAck.Header.Clone();
                        Protocol.InventoryCodec.WriteDeleteAck(cabD, corpo, antesDeApagar,
                                                               catalogo, instancia);
                        await SendAsync(new ResponseMap.Message(dAck.Id, cabD, corpo), ct);
                        continue;
                    }
                    Log("   (WARNING: no recorded DeleteItemAck; the del_s1 recording is needed)");
                }

                // O MountItemReq nao pede "equipa este": manda a TABELA de montagem
                // inteira, e o servidor devolve-a tal e qual prefixada por `01 00`.
                // Medido em captures/skin.pcapng, duas vezes byte a byte:
                //
                //   C 0x00d7   C627350118A80100C6273501FFFF...          (60 B)
                //   S 0x00d8   0100 C627350118A80100C6273501FFFF...     (62 B)
                //
                // A aritmetica fecha: 67 - 7 = 60 no pedido, 69 - 7 = 62 na resposta.
                //
                // Antes eu mandava o corpo GRAVADO com so' a instancia remendada em +2,
                // portanto os restantes slots iam com o estado de quem gravou a captura.
                // O cliente rele' a sua tabela daqui, e por isso o inventario voltava
                // sempre ao equipamento da gravacao mal se reabria o ecra.
                //
                // A tabela le-se: `+0` = instancia do item que esta' a ser montado, e a
                // partir de `+4` pares `[catalogo][instancia]` dos que ja' estavam. Um par
                // a 0xFF e' um slot vazio — e' assim que o cliente anuncia que largou o
                // item anterior daquela categoria.
                if (Account is not null && id == Protocol.RequestId.MountItemReq && plain.Length >= 4)
                {
                    uint entrou = BitConverter.ToUInt32(plain, 0);
                    var jaMontados = new List<(uint Cat, uint Inst)>();
                    for (int p = 4; p + 8 <= plain.Length; p += 8)
                    {
                        uint cat = BitConverter.ToUInt32(plain, p);
                        if (cat == 0xFFFFFFFF) continue;
                        jaMontados.Add((cat, BitConverter.ToUInt32(plain, p + 4)));
                    }

                    foreach (var it in Account.Items)
                        it.Equipped = (entrou != 0xFFFFFFFF && it.InstanceId == entrou)
                                   || jaMontados.Any(m => m.Cat == it.CatalogId && m.Inst == it.InstanceId);
                    Store?.Save();
                    Log($"   (equip: instance {entrou}, " +
                        $"{Account.Items.Count(i => i.Equipped)} equipped)");

                    var ack = FindAnywhere(Protocol.RequestId.MountItemAck)
                                  .FirstOrDefault(m => m.Id == Protocol.RequestId.MountItemAck);
                    if (ack.Header is not null && ack.Header.Length >= 7 && packet.Length >= 5)
                    {
                        var corpo = new byte[2 + plain.Length];
                        corpo[0] = 0x01;
                        plain.CopyTo(corpo, 2);

                        // O cabecalho anuncia QUAL o item montado: `A7 00` seguido dos 16
                        // bits baixos do catalogo, os mesmos que vem no cabecalho do
                        // pedido (bytes 3..4). Confirmado em quatro amostras:
                        //   Camellia -> A7 00 06 8C     mascara -> A7 00 20 88
                        //
                        // Reenviar o cabecalho gravado anunciava sempre a Camellia, e o
                        // cliente punha-a no slot do avatar fosse qual fosse o item
                        // equipado. As skins de notas escapavam porque a identidade delas
                        // vem dos pares do corpo, nao do cabecalho.
                        var cab = (byte[])ack.Header.Clone();
                        cab[5] = packet[3];
                        cab[6] = packet[4];

                        await SendAsync(new ResponseMap.Message(ack.Id, cab, corpo), ct);
                        continue;
                    }
                    Log("   (WARNING: no recorded MountItemAck)");
                }

                // Atualizacao do icone do jogador no lobby. Depois de equipar, o cliente
                // manda `0x0023` e o servidor responde `0x0024 UpdateUserIconInf`. Tres
                // amostras em captures/equip2.pcapng, com dois itens diferentes:
                //
                //   Camellia (101382 = 0x18C06)   corpo = 1F00 068C 0000...
                //   mascara  (100384 = 0x18820)   corpo = 1F00 2088 0000...
                //
                // So' `+2..3` mudam, e sao os 16 bits baixos do catalogo. Nao e' preciso
                // deduzi-los: o proprio pedido ja' os traz no cabecalho, bytes 3..4
                // (`230044 068C0000`, `2300D9 20880000`), por isso e' um eco direto.
                //
                // Sem isto o servidor reenviava o 0x0024 gravado tal e qual, e o lobby
                // mostrava sempre o item de quem gravou a captura, equipasse o jogador o
                // que equipasse.
                if (id == UserIconReq && packet.Length >= 5)
                {
                    ushort avatar = BitConverter.ToUInt16(packet, 3);
                    if (Account is not null) { Account.Avatar = avatar; Store?.Save(); }

                    var icone = FindAnywhere(UserIconInf).FirstOrDefault(m => m.Id == UserIconInf);
                    if (icone.Body is not null && Protocol.UserIcon.CanPatch(icone.Body))
                    {
                        var corpo = (byte[])icone.Body.Clone();
                        Protocol.UserIcon.Write(corpo, _waiterIndex, avatar);
                        await SendAsync(new ResponseMap.Message(icone.Id, icone.Header, corpo), ct);
                        Log($"   (icon: avatar 0x{avatar:x4} at index {_waiterIndex})");
                        continue;
                    }
                    Log("   (WARNING: no recorded 0x0024; the lobby icon will not change)");
                }

                // A escolha do jogador: a musica vai em claro no cabecalho, a dificuldade
                // e' o byte 0 do corpo cifrado. O bloco que o servidor devolve e' o chart,
                // por isso as duas coisas sao precisas para servir o certo.
                if (id == ChangeDiscReq && packet.Length >= 7 && plain.Length >= 1)
                {
                    uint pedida = BitConverter.ToUInt32(packet, 3);

                    // O QUE O CLIENTE PEDE E O QUE A BIBLIOTECA CONHECE PODEM SER NUMEROS
                    // DIFERENTES. Ver Net.SongIdMap: o id e' a posicao no DiscStock.csv do
                    // cliente, e o de 2007 tem 94 musicas onde o nosso tem 277.
                    uint naBiblioteca = _mapaDeMusicas?.ParaBiblioteca(pedida) ?? pedida;
                    if (naBiblioteca != pedida)
                        Log($"   (song id translated: {pedida} (client) -> {naBiblioteca} (library))");
                    else if (_mapaDeMusicas is not null)
                        Log($"   (WARNING: the map does not know song {pedida}; sending it untranslated)");

                    _selected = new Protocol.SongKey(naBiblioteca, plain[0]);

                    // O ecra de resultado tem de nomear a musica COMO O CLIENTE A CONHECE.
                    _musicaEmCurso = pedida;

                    // Musica nova: recomecar a contagem de breaks. O HP fica por
                    // determinar ate' o cliente o reportar — depende dos itens equipados.
                    _hp = null; _breaks = 0; _precisao = 0; _lastReport = null;
                    _musicVideo = Protocol.MusicVideo.Requested(plain);
                    // O corpo tem 10 bytes e so' o primeiro esta' identificado. Os
                    // restantes devem trazer opcoes da jogada â€” o botao "music video",
                    // por exemplo, nao produz nenhuma mensagem distinta, logo so' pode
                    // vir aqui. Registados por inteiro para se poder comparar depois.
                    Log($"   (choice: {Catalogo?.Nome(_selected.Value.Song) ?? ""} — {_selected}; " +
                        $"body {Convert.ToHexString(plain)})");
                }

                // Fim de musica. Observado no servidor real (Tools/Timeline sobre
                // free_s1): o cliente manda StageResultInf com o resultado, logo a seguir
                // PlayOverReq, e ~40 ms depois o servidor responde com cinco mensagens â€”
                // inventario, propriedades, StageResultExInf, PlayOverInf e ReadyInf. Sao
                // essas que fazem o cliente sair do ecra de fim e voltar a' lista; sem
                // elas fica preso ate' se carregar em ESC.
                //
                // O gatilho e' o PlayOverReq. Na gravacao o conjunto ficou no balde do
                // estado periodico, porque um 0x0072 passou entre o pedido e a resposta â€”
                // por isso procura-se por CONTEUDO (quem traz o PlayOverInf) e nao por
                // pedido.
                if (id == StageResultInf)
                {
                    _stageFinished = true;
                    // Guardar o relatorio do cliente: e' de la' que saem o combo e o MAX
                    // ganho que vao para o ecra de resultados.
                    _lastReport = Protocol.StageResult.CanReadReport(plain) ? plain : null;
                    Log("   (result received" +
                        (_lastReport is not null
                            ? $": {BitConverter.ToUInt16(_lastReport, Protocol.StageResult.ReportTotalNotes)} notes, " +
                              $"combo {BitConverter.ToUInt32(_lastReport, Protocol.StageResult.ReportMaxCombo)}, " +
                              $"+{BitConverter.ToUInt16(_lastReport, Protocol.StageResult.ReportMaxGain)} MAX)"
                            : ")"));
                }

                if (id == PlayOverReq)
                {
                    _stageFinished = false;
                    // EM COURSE MODE O FECHO TEM DE VIR DA GRAVACAO DO COURSE.
                    //
                    // A procura normal vai a' gravacao principal primeiro, que e' de free
                    // mode, e trazia de la' o fecho errado. Medido comparando o meu trafego
                    // com o do servidor real na mesma situacao:
                    //
                    //   0x0070  real 000000FF...   meu 00000000...   (byte +3)
                    //   0x005E  real 000000...     meu 390000...     (byte +0)
                    //
                    // O 0x005E ReadyInf e' o que prepara a musica seguinte do course, e
                    // levava o valor de uma jogada de free mode. O cliente fechava-se logo
                    // a seguir ao ecra de resultados da primeira musica.
                    // ... e tem de ser o fecho DESTA etapa, nao o da primeira.
                    //
                    // A gravacao tem um fecho por musica do course, e cada um anuncia a
                    // musica seguinte no 0x005E. Pedir sempre o primeiro fazia o cliente
                    // mostrar os resultados da primeira musica no fim da segunda, e propor
                    // outra vez a musica que o jogador acabara de fazer.
                    //
                    // O _courseSong ja' foi incrementado no arranque, por isso a etapa em
                    // curso e' a anterior.
                    List<ResponseMap.Message> closing;
                    if (_courseRoom)
                    {
                        var fonteC = new[] { _map }.Concat(Extras)
                            .FirstOrDefault(f => f.Nome == Config.GravacaoCourse);
                        var fechos = fonteC?.AllSetsContaining(PlayOverInf)
                                     ?? new List<IReadOnlyList<ResponseMap.Message>>();
                        int etapa = Math.Max(0, _courseSong - 1);

                        // So' ha' fechos gravados para as musicas do course que foi
                        // capturado — duas, porque a ultima nao leva PlayOverInf. Um course
                        // de quatro, cinco ou seis musicas passa disso, e nesse caso repete-se
                        // o ultimo gravado: as mensagens intermedias sao equivalentes entre
                        // si, o que muda de verdade e' a ultima, e essa e' tratada a' parte
                        // (na ultima musica so' sai o 0x002A).
                        int usado = fechos.Count > 0 ? Math.Min(etapa, fechos.Count - 1) : -1;
                        closing = usado >= 0
                            ? fechos[usado].ToList()
                            : FindAnywhere(PlayOverInf).ToList();
                        Log($"   (course closing: stage {etapa + 1}" +
                            (usado != etapa ? $", repeating recorded closing {usado + 1}" : "") +
                            $" of {fechos.Count} in {Config.GravacaoCourse})");
                    }
                    else closing = FindAnywhere(PlayOverInf).ToList();
                    if (closing.Count > 0)
                    {
                        // O perfil avanca aqui. Sem isto as mensagens de fecho sao copias
                        // da gravacao e a barra de experiencia nunca mexe.
                        // UMA MUSICA FALHADA NAO DA' NADA. No 0x0070 real de uma etapa que
                        // vai abaixo o XP ganho (+35) e o MAX ganho (+37) sao ZERO; o meu
                        // dava-os na mesma, e chegou a subir a conta de nivel por ter
                        // falhado. Ver Protocol.StageResult.MarcarEtapaFalhada.
                        //
                        // E NAO E' SO' NOS COURSES — a condicao era `!(_courseRoom &&
                        // _etapaFalhada)`, que no free mode dava sempre verdadeira. MEDIDO NO
                        // SERVIDOR REAL, na gravacao end_s1: nove musicas de free mode
                        // seguidas, as cinco primeiras concluidas e as quatro ultimas com game
                        // over. No 0x0070 de cada uma, o XP concedido (+37) e o MAX (+39):
                        //
                        //     concluidas   37/13  39/15  38/18  38/10  28/17
                        //     game over     0/0    0/0    0/0    0/0
                        //
                        // e o 0x0025 confirma-o do outro lado — o XP parou nos 180 e o MAX nos
                        // 14895 e nao voltaram a mexer nas quatro ultimas.
                        if (_profile is not null && !_etapaFalhada)
                        {
                            int maxGanho = _lastReport is not null
                                ? BitConverter.ToUInt16(_lastReport, Protocol.StageResult.ReportMaxGain)
                                : 0;
                            int xpGanho = PlayerProfile.XpDaJogada(_precisao, _breaks);

                            // O BONUS DOS ITENS EQUIPADOS. O cliente aplica o HP sozinho — ve-se
                            // no perfil, "HP 195 (130+65)", sem o servidor mexer em nada — mas o
                            // ganho de EXP e de MAX que ele reporta e' o BASE. Estava a ir para a
                            // conta tal e qual, e por isso a gear nao fazia diferenca nenhuma.
                            // Ver Net.ItemTable.
                            if (Itens is { Count: > 0 } tabela && Account is not null)
                            {
                                var (bExp, bMaxPct, bMaxFixo, bHp) = tabela.Bonus(Account.Items);
                                if (bExp > 0 || bMaxPct > 0 || bMaxFixo > 0)
                                {
                                    int xpBase = xpGanho, maxBase = maxGanho;
                                    xpGanho = (int)Math.Round(xpGanho * (1 + bExp / 100.0));
                                    maxGanho = (int)Math.Round(maxGanho * (1 + bMaxPct / 100.0) + bMaxFixo);
                                    Log($"   (equipped items: exp +{bExp:0.#}% ({xpBase}->{xpGanho}), " +
                                        $"MAX +{bMaxPct:0.#}% +{bMaxFixo:0.#} ({maxBase}->{maxGanho}), " +
                                        $"HP +{bHp} (from the client))");
                                }
                            }

                            // E O QUE O PAINEL DE PERFIL MOSTRA: combo maximo e precisoes.
                            // Sem isto ficavam para sempre os numeros de quem gravou.
                            Account?.RegistarActuacao(_precisao, _lastReport is not null
                                ? (int)BitConverter.ToUInt32(_lastReport, Protocol.StageResult.ReportMaxCombo)
                                : 0);

                            bool subiu = _profile.CompleteSong(maxGanho, xpGanho);
                            if (subiu) _subiuDeNivel = true;

                            // O QUE FOI MESMO CONCEDIDO, para o ecra de resultado o poder
                            // mostrar. Ver onde e' escrito, no 0x0070.
                            _xpConcedido = xpGanho;
                            _maxConcedido = maxGanho;

                            Log($"   (profile: {_profile}{(subiu ? "  *** LEVELLED UP ***" : "")})");
                        }
                        else if (_etapaFalhada)
                        {
                            _xpConcedido = _maxConcedido = 0;
                            Log($"   ({(_courseRoom ? "etapa" : "musica")} failed: no XP and no MAX)");
                        }

                        // E O SORTEIO DO ITEM, so' nas etapas que passam.
                        // O DISCO DE CADA MUSICA E' A MEDALHA DA ACTUACAO, e sai da precisao.
                        // Ver Protocol.DefaultItems.DiscoDaActuacao.
                        //
                        // NAO E' SO' NO COURSE: o free mode tambem os da'. O ecra de resultado
                        // de uma musica solta anuncia-o na mesma — "GOLDEN DISC" com 96,54% —
                        // e a coleccao tem de o registar. So' a etapa FALHADA nao da' nada.
                        if (ItensDaConta && !_etapaFalhada &&
                            Protocol.DefaultItems.DiscoDaActuacao(_precisao) is { } medalha)
                            Premiar(medalha, $"medal ({_precisao:0.00}%)");
                        //
                        // O DA CONCLUSAO esse e' fixo (DiscNum do course, 1056) e nao depende
                        // da actuacao. Testa-se pela ULTIMA ETAPA e nao pelo _fimDeCourse: esse
                        // so' e' posto a seguir, no bloco do fim de course, e por isso ainda
                        // estava a false aqui — era por isso que o disco nao era atribuido.
                        //
                        // E SO' SE O COURSE FOR MESMO PASSADO. Um course levado ate' ao fim sem
                        // cumprir as condicoes nao da' premio nenhum: na captura do "Fine Day"
                        // falhado o servidor original nao mandou o `0x0089`, e o MAX final ficou
                        // na soma crua das etapas (2+4+3=9) sem o bonus de +30% do course.
                        // MODO RANKING: soma-se a etapa e, na terceira, ve-se se o total bate o
                        // recorde. Tem de ser AQUI e nao no ecra, porque o 0x0025 que leva o
                        // recorde sai nesta mesma rajada, antes do 0x0070.
                        if (_tipoSala == RoomTypeRanking && !_etapaFalhada) SomarEtapaDeRanking();

                        // O RECORDE DE FREE MODE TAMBEM SE DECIDE AQUI, e nao no ecra. O
                        // 0x0025 que o leva sai NESTA rajada, antes do 0x0070 onde ele era
                        // calculado — por isso o painel mostrava sempre o recorde anterior, e
                        // numa conta nova mostrava zero depois de uma musica que o estabeleceu.
                        if (!_courseRoom && _tipoSala != RoomTypeRanking && !_etapaFalhada)
                            ActualizarRecordeDeFreeMode();

                        if (_courseRoom && Courses?.Get(_courseIndex) is { } cFim &&
                            _courseSong >= cFim.Musicas.Count)
                        {
                            _courseFalhado = !_etapaFalhada && DecidirCourse(cFim);
                            if (ItensDaConta && !_etapaFalhada && !_courseFalhado)
                            {
                                PremiarDiscoDoCourse();
                                SortearItemDoCourse();
                            }
                        }
                        // ULTIMA MUSICA DE UM COURSE: so' sai o 0x002A.
                        //
                        // Medido na gravacao course2_s1, comparando os tres fins:
                        //   musicas 1 e 2: 0x002A 0x0025 0x0070 0x006B 0x005E
                        //   musica 3:      0x002A            <- e mais nada
                        //
                        // O 0x005E ReadyInf e' o que manda o cliente preparar-se para a
                        // musica seguinte. Envia-lo no fim do course faz o cliente pedir uma
                        // musica que nao existe — era a "quarta musica" fantasma, que no
                        // caso do Let's Begin aparecia como a 1st sync.
                        //
                        // Quantas musicas tem o course sabe-se agora pelo courses.txt, que
                        // vem do CourseSection.ini do proprio jogo (campo Song).
                        var cursoActual = _courseRoom ? Courses?.Get(_courseIndex) : null;
                        if (cursoActual is { } cc && _courseSong >= cc.Musicas.Count)
                        {
                            // E TEM DE SER O 0x002A DO FIM, nao o do meio.
                            //
                            // Os fechos sao procurados pelo PlayOverInf, que a ultima etapa
                            // nao tem — por isso so' aparecem dois num course de tres, e a
                            // terceira acabava a repetir o 0x002A da segunda. Na course2_s1
                            // os dois nao sao iguais:
                            //
                            //   etapa 2:  06 04 1C 00 05 04 15 00 ... 20 04 03 00
                            //   etapa 3:  06 04 1C 00 05 04 16 00 ... 20 04 04 00
                            //
                            // Sao contadores de item que avancam com a jogada. Procura-se o
                            // ULTIMO grupo ancorado no proprio 0x002A, que e' o do fim do
                            // course.
                            var fimGravado = new[] { _map }.Concat(Extras)
                                .FirstOrDefault(f => f.Nome == Config.GravacaoCourse)
                                ?.AllSetsContaining(0x002A);
                            var ultimo = fimGravado is { Count: > 0 }
                                ? fimGravado[^1].FirstOrDefault(m => m.Id == 0x002A)
                                : default;

                            // TIRA-SE O 0x005E, MAS NAO O 0x006B.
                            //
                            // O 0x005E ReadyInf e' o que manda o cliente preparar a musica
                            // seguinte; no fim do course isso faz aparecer uma quarta musica
                            // fantasma. Mas o 0x006B PlayOverInf e' o que FECHA a jogada, e
                            // cortar tambem esse deixava o cliente dentro da musica: no log
                            // via-se ele a continuar a mandar 0x006C indefinidamente depois
                            // do ultimo 0x006A, quando nas etapas 1 e 2 parava logo.
                            //
                            // As duas gravacoes mostram o servidor real a mandar so' o 0x002A
                            // (course2_s1) ou mesmo nada (course_s1) neste ponto — mas nessas
                            // o cliente saiu, e o meu nao sai. Ate' perceber porque, vale mais
                            // fechar a jogada explicitamente do que deixa-la aberta.
                            // SAI TUDO MENOS O 0x005E, que pede a musica seguinte e no fim do
                            // course faz aparecer uma quarta fantasma.
                            //
                            // O 0x0070 TEM de ir: o ecra final le' dele os numeros do Total
                            // Result. Medido nos dois sentidos — com --sem-fecho, que serve o
                            // 0x0070 gravado, o Total Result saiu com os valores da gravacao
                            // (COMBO 146, BREAK 2); tirando o 0x0070 de todo, saiu a ZEROS.
                            // O cliente nao acumula nada por si.
                            // O FECHO DA ULTIMA ETAPA E' O COMPLETO, 0x005E incluido.
                            // Medido na course3_s1, que acabou em COURSE SUCCESS. O que marca
                            // o fim sao tres bytes do 0x0070 — ver StageResult.MarcarFimDeCourse.
                            _fimDeCourse = true;

                            // E o 0x002A tem de ser o do FIM. Os fechos sao procurados pelo
                            // PlayOverInf, que a ultima etapa gravada nao tem, por isso so'
                            // aparecem dois num course de tres e a terceira acabava a repetir
                            // o da segunda. Na course2_s1 os dois diferem em dois contadores.
                            if (ultimo.Header is not null)
                                closing = closing
                                    .Select(m => m.Id == 0x002A ? ultimo : m)
                                    .ToList();

                            Log($"   (end of course \"{cc.Nome}\": last of {cc.Musicas.Count} songs; " +
                                $"sending {string.Join(" ", closing.Select(m => $"0x{m.Id:x2}"))}" +
                                (ultimo.Header is not null ? ", recorded end 0x002a" : "") +
                                "; the final screen is the client's)");
                        }

                        // ETAPA FALHADA: o fecho espera pelo 0x0087. O servidor real nao
                        // responde ao 0x006A quando a etapa foi abaixo — deixa passar o
                        // 0x0087 e so' entao manda 0x0088 + 0x0025 0x0070 0x006B 0x005E, por
                        // esta ordem. Mandar o fecho primeiro e o 0x0088 a seguir fechava o
                        // cliente.
                        //
                        // O sinal e' deterministico, nao e' tempo: se o jogador escolher
                        // "nao continuar", o cliente NAO chega sequer a mandar o
                        // 0x00E7/0x006F/0x006A — sai da sala com um 0x0073. Logo, um 0x006A
                        // com a barra a zero implica sempre um 0x0087 a seguir.
                        // Ver Protocol.StageEndReport.
                        if (_courseRoom && _etapaFalhada)
                        {
                            // E SEM O 0x002A. Numa etapa PASSADA o servidor real abre o fecho
                            // com o UpdInvDefaultItemInf; numa FALHADA nao o manda. Medido
                            // pondo os dois fechos lado a lado — o real na fail_s1 e o meu
                            // numa captura de loopback da mesma situacao:
                            //
                            //   real   0x0088          0x0025 0x0070 0x006B 0x005E
                            //   meu    0x0088  0x002A  0x0025 0x0070 0x006B 0x005E
                            //
                            // O 0x0088 saia byte a byte igual e a rajada da etapa repetida
                            // tambem (bloco e as cinco pequenas, identicos a' primeira
                            // tentativa) — o 0x002A a mais era a unica diferenca, e era o que
                            // fechava o cliente.
                            _fechoEmEspera = closing.Where(m => m.Id != 0x002A).ToList();
                            Log($"   (stage failed: closing held back waiting for the 0x0087, " +
                                $"{_fechoEmEspera.Count} messages, without the 0x002a)");
                            continue;
                        }

                        Log($"   (end of song: {closing.Count} closing messages)");
                        foreach (var m in closing) await SendAsync(m, ct);
                        continue;
                    }
                    Log("   (WARNING: no recorded song closing; the client will hang)");
                }

                Log($"<- 0x{id:x4} {packet.Length}B (#{n + 1})  =>  {replies.Count} reply(ies)");
                foreach (var m in replies) await SendAsync(m, ct);

                if (id == ContinueCourseReq && _fechoEmEspera is { Count: > 0 } fecho)
                {
                    _fechoEmEspera = null;

                    // RECUAR A ETAPA. O contador ja' avancou quando a rajada desta etapa foi
                    // montada, e a etapa seguinte e' escolhida com ele ANTES de aqui se
                    // chegar — filtrar no incremento nao servia, escolhia na mesma a errada.
                    _repetirEtapa = true;
                    if (_courseSong > 0) _courseSong--;
                    Log($"   (continue course: the held closing goes out, {fecho.Count} messages; " +
                        $"repeating stage {_courseSong + 1})");
                    // CONTINUAR CUSTA MAX: cada tentativa paga o preco do course outra vez.
                    // O preco vem do MaxPrice do CourseSection.ini, ja' no courses.txt.
                    var cursoPago = Courses?.Get(_courseIndex);
                    if (_profile is not null && cursoPago is { Preco: > 0 } cp)
                    {
                        int antes = _profile.Max;
                        _profile.Max = Math.Max(0, _profile.Max - cp.Preco);
                        Store?.Save();
                        Log($"   (continue course: -{cp.Preco} MAX, {antes} -> {_profile.Max})");
                    }

                    // O _etapaFalhada so' se limpa DEPOIS de o fecho sair: e' no SendAsync
                    // que o 0x0070 leva a marca de etapa falhada, e limpa-lo antes deixava a
                    // marca por escrever.
                    foreach (var m in fecho) await SendAsync(m, ct);
                    _etapaFalhada = false;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { Log($"IO ended: {ex.Message}"); }
        catch (Exception ex) { Log($"ERROR: {ex}"); }
    }

    /// <summary>
    /// Envia o ConnectAck gravado, tal e qual.
    ///
    /// Gerar um novo exigiria conhecer ao pormenor como o cliente deriva a chave dos 32
    /// bytes que ele transporta, e essa forma ainda nao esta' toda esclarecida â€” os dois
    /// ultimos bytes sao sempre zero no original, sinal de que ha' ali mais estrutura.
    /// Preencher essa zona com entropia partia a sessao de maneira erratica.
    ///
    /// Reenviar o original resolve: a chave passa a ser uma que ja' se sabe funcionar.
    /// O endereco que ele anuncia e' irrelevante â€” o cliente liga-se ao canal pelo
    /// endereco do ChannelInfoInf, que e' reescrito.
    /// </summary>
    private async Task SendConnectAckAsync(byte[] pedido, CancellationToken ct)
    {
        // DIAGNOSTICO --connectack-de: so' o ConnectAck troca de gravacao.
        //
        // Separa as duas coisas que o --serve-game troca ao mesmo tempo: o ConnectAck (que
        // define a chave de sessao) e tudo o resto. O --login-de ja' cobre a rajada de login
        // e nao chegou; se for este, e' o ConnectAck e mais nada.
        var ack = _map.ConnectAck;
        if (ConnectAckDe is not null)
        {
            // Aceita "ficheiro:hdr" (so' o cabecalho, 7 bytes, que viaja em claro) e
            // "ficheiro:corpo" (so' os 40 bytes de onde sai a chave de sessao), para partir
            // o ConnectAck ao meio e ver qual das metades e' que importa.
            var nome = ConnectAckDe;
            string? parte = null;
            int dp = nome.LastIndexOf(':');
            if (dp > 0) { parte = nome[(dp + 1)..]; nome = nome[..dp]; }

            var outra = new[] { _map }.Concat(Extras).FirstOrDefault(x => x.Nome == nome);
            if (outra is { ConnectAck.Length: 47 })
            {
                if (parte is null)
                {
                    ack = outra.ConnectAck;
                    Log($"   (ConnectAck served by {nome})");
                }
                else
                {
                    var misto = (byte[])ack.Clone();
                    if (parte == "hdr") Array.Copy(outra.ConnectAck, 0, misto, 0, 7);
                    else Array.Copy(outra.ConnectAck, 7, misto, 7, 40);
                    ack = misto;
                    Log($"   (ConnectAck: {parte} coming from {nome})");
                }
            }
            else Log($"   (WARNING: --connectack-de {nome} is not loaded or has no ConnectAck)");
        }
        if (ack.Length != 47)
            throw new InvalidOperationException("the capture did not carry a usable ConnectAck");

        // A data do servidor tambem viaja aqui, e e' esta a PRIMEIRA que o cliente ve'.
        // Corrigir so' a do LogInAck deixava-o com duas datas em desacordo. Ver
        // Protocol.LogInAck.ConnectAckDateOffset — e' preciso corrigir ANTES de derivar a
        // chave, porque estes bytes fazem parte do material de onde ela sai.
        if (!Replica && Protocol.LogInAck.PodeCorrigirConnectAck(ack))
        {
            ack = (byte[])ack.Clone();
            var (an, me, di) = Protocol.LogInAck.LerDoConnectAck(ack);
            Protocol.LogInAck.EscreverNoConnectAck(ack, DateTime.Now);
            Log($"   (ConnectAck: date {an:0000}-{me:00}-{di:00} -> today)");
        }

        // O indice do jogador no lobby viaja no cabecalho deste pacote, em claro. E' a
        // primeira coisa que o cliente sabe sobre si proprio, por isso e' esta a fonte da
        // verdade: todas as outras mensagens que o transportam passam a ser reescritas com
        // ele. Ver Protocol.LogInAck.ConnectAckIndexOffset.
        _waiterIndex = Protocol.LogInAck.LerIndiceDoConnectAck(ack);

        // A VERSAO DO CLIENTE viaja no ConnectReq, em [3..6] e em claro. O nosso cliente
        // anuncia 0x00040201 ("v4.2 T1"); o original chines da SNDA 2.60 anuncia 0x00020006.
        // Ver Protocol.ClientVersion.
        uint versao = Protocol.ClientVersion.Ler(pedido);
        ushort idAck = Protocol.ClientVersion.IdDoConnectAck(versao);
        if (idAck != Protocol.ClientVersion.IdConnectAckPadrao)
        {
            ack = (byte[])ack.Clone();
            BitConverter.TryWriteBytes(ack, idAck);
        }
        _versaoDoCliente = versao;

        // O CATALOGO DE MUSICAS E' OUTRO NAS VERSOES ANTIGAS, e o id de rede e' a posicao no
        // catalogo — logo os ids nao querem dizer o mesmo. Ver Net.SongIdMap.
        _mapaDeMusicas = Protocol.ClientVersion.EDe2007(versao) ? MusicasSnda : null;

        // Sempre no log: se o cliente nao for o do costume, e' isto que o diz.
        Log($"   (client {Protocol.ClientVersion.Nome(versao)} = 0x{versao:x8}; " +
            $"ConnectAck goes out as 0x{idAck:x4})");
        if (_mapaDeMusicas is { Count: > 0 } mp)
            Log($"   (song ids translated by {mp.Count} pairs — 2007 catalogue)");
        else if (Protocol.ClientVersion.EDe2007(versao))
            Log("   (WARNING: 2007 client without dados/musicas-snda.txt; the ids go untranslated " +
                "and the songs will come out swapped)");

        await _stream.WriteAsync(ack, ct);   // em claro: o cliente so' cifra depois disto

        var key = PacketCodec.TransformSessionKey(ack.AsSpan(7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        // Uma cifra por sentido, ambas a partir da mesma chave e com o mesmo estado
        // inicial: os fluxos sao independentes, cada um avanca com o que passa nele.
        _outgoing = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);
        _incoming = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        Log($"-> ConnectAck 47B (recorded); key {Convert.ToHexString(key)[..16]}...");

        foreach (var m in _map.Greeting) await SendAsync(m, ct);
    }

    /// <summary>
    /// O MELHOR RESULTADO DE SEMPRE, nas tres mensagens que o transportam.
    ///
    /// Esteve desligado por o offset nunca ter sido confirmado. Esta' agora: procurando o
    /// 252880 que o painel de perfil mostrava — o recorde de quem gravou — ele aparece em
    /// tres sitios e em mais nenhum, os mesmos em duas gravacoes independentes:
    /// <c>0x0043</c> +85, <c>0x0051</c> +80 e <c>0x0025</c> +26.
    ///
    /// E' o "自由模式最高得分 / LIGHT CHANNEL 5KEYS" do ecra de perfil.
    /// </summary>
    private void EscreverRecorde(byte[] body, int offset) =>
        EscreverRecorde(body, offset, Account?.RecordeDoCanal(Canal) ?? 0);

    private void EscreverRecorde(byte[] body, int offset, int valor)
    {
        if (Account is null || body.Length < offset + 4) return;
        BitConverter.TryWriteBytes(body.AsSpan(offset, 4), (uint)Math.Max(0, valor));
    }

    /// <summary>
    /// O painel de perfil mostra OS DOIS CANAIS AO MESMO TEMPO — "LIGHT CHANNEL 5KEYS" e
    /// "MANIA CHANNEL 7KEYS", lado a lado —, por isso esta mensagem leva os dois recordes,
    /// independentemente do canal em que a sessao esta'.
    ///
    /// Os outros sitios onde o recorde viaja (<c>0x0051</c>, <c>0x0025</c>) tem uma so' caixa
    /// e levam o do canal da sessao.
    /// </summary>
    /// <summary>
    /// O RESTO DO PAINEL DE PERFIL: sexo, ano de nascimento, avatar, combo maximo e precisoes.
    ///
    /// Sem isto uma conta acabada de criar aparecia com os numeros de quem gravou a captura —
    /// 453 de combo maximo, 100,00% de melhor precisao, 87,27% de media — e com "M" no sexo
    /// mesmo tendo escolhido feminino.
    ///
    /// O sexo muda de contagem entre mensagens: o cliente manda-o 1-baseado no <c>0x0032</c> e
    /// o painel le'-o 0-baseado. Ver Protocol.UserInfo.SexoOffset.
    /// </summary>
    private void EscreverPerfilDaConta(byte[] body)
    {
        if (Account is null) return;

        if (Account.Sexo > 0 && body.Length > Protocol.UserInfo.SexoOffset)
            body[Protocol.UserInfo.SexoOffset] = (byte)(Account.Sexo - 1);

        if (Account.Idade > 0 && body.Length >= Protocol.UserInfo.AnoNascimentoOffset + 2)
            BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserInfo.AnoNascimentoOffset, 2),
                                       (ushort)(DateTime.Now.Year - Account.Idade));

        if (body.Length >= Protocol.UserInfo.CreditosOffset + 4)
            BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserInfo.CreditosOffset, 4),
                                       (uint)Math.Max(0, Account.Creditos));

        if (body.Length >= Protocol.UserInfo.MaxComboOffset + 4)
            BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserInfo.MaxComboOffset, 4),
                                       (uint)Math.Max(0, Account.MaxCombo));

        if (body.Length >= Protocol.UserInfo.BestAccuracyOffset + 4)
            BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserInfo.BestAccuracyOffset, 4),
                                       (uint)Math.Round(Account.MelhorPrecisao * 100));

        if (body.Length >= Protocol.UserInfo.AvgAccuracyOffset + 2)
            BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserInfo.AvgAccuracyOffset, 2),
                                       (ushort)Math.Round(Account.PrecisaoMedia * 100));

        Log($"   (profile: sex {(Account.Sexo == 1 ? "F" : Account.Sexo == 2 ? "M" : "?")}, " +
            $"{Account.Idade} years old, max combo {Account.MaxCombo}, " +
            $"best accuracy {Account.MelhorPrecisao:0.00}% average {Account.PrecisaoMedia:0.00}%, " +
            $"{Account.Creditos} credits)");
    }

    private void EscreverOsDoisRecordes(byte[] body)
    {
        if (Account is null) return;
        EscreverRecorde(body, Protocol.UserInfo.BestScoreOffset, Account.BestScore);
        EscreverRecorde(body, Protocol.UserInfo.BestScore7KOffset, Account.BestScore7K);
        EscreverRecorde(body, Protocol.UserInfo.RankingScoreOffset, Account.RankingScore);
        EscreverRecorde(body, Protocol.UserInfo.RankingScore7KOffset, Account.RankingScore7K);
    }

    private Dictionary<ushort, int> ContagensDaConta() =>
        Account?.DefaultItems
            .Where(e => ushort.TryParse(e.Key, out _))
            .ToDictionary(e => ushort.Parse(e.Key), e => e.Value)
        ?? new Dictionary<ushort, int>();

    /// <summary>
    /// O premio de fim de etapa: UM item de omissao com +1, sorteado. Medido nas tres
    /// gravacoes de course — ha' sempre exactamente um id a subir de uma etapa para a
    /// seguinte, e o id varia. Ver Protocol.DefaultItems.
    /// </summary>
    private void PremiarItemDeEtapa()
    {
        if (Account is null) return;

        // Quem escolhe o disco e' O SERVIDOR. O cliente nunca o envia: procurei o id que
        // realmente subiu em cada etapa (0x0406 na course2_s1, 0x0403 na course_s1, 0x0406
        // na course3_s1) nas TRES mensagens que ele manda no fim da etapa — 0x006F, 0x00E7 e
        // 0x0072 — e nao esta' em nenhuma. O que o jogador ve' e' a diferenca que o 0x002A
        // lhe der.
        var pool = Account.DefaultItems.Keys
            .Select(k => ushort.TryParse(k, out var v) ? v : (ushort)0).Where(v => v != 0).ToArray();
        if (pool.Length == 0) pool = Protocol.DefaultItems.Conhecidos;

        ushort id = pool[Random.Shared.Next(pool.Length)];
        Premiar(id, "stage");
    }

    /// <summary>Soma um ao disco indicado e guarda.</summary>
    private void Premiar(ushort id, string porque)
    {
        if (Account is null) return;
        string k = id.ToString();
        Account.DefaultItems[k] = Account.DefaultItems.GetValueOrDefault(k) + 1;
        Store?.Save();
        Log($"   (prize for {porque}: disc {Protocol.DefaultItems.Discos.Nome(id)} " +
            $"(0x{id:x4}) -> {Account.DefaultItems[k]})");
    }

    /// <summary>
    /// O premio de CONCLUSAO do course, que nao e' sorteado: vem do <c>DiscNum</c> do
    /// CourseSection.ini (ja' no courses.txt como <c>disco=</c>). Vale 1056 = 0x0420 em todos
    /// os 43, e e' exactamente o id que sobe no fim do course nas tres gravacoes.
    /// </summary>
    private void PremiarDiscoDoCourse()
    {
        if (Courses?.Get(_courseIndex) is { DiscoPremio: > 0 } c)
            Premiar((ushort)c.DiscoPremio, $"conclusao do \"{c.Nome}\"");
    }

    /// <summary>
    /// O SORTEIO DE ITEM da conclusao, do campo <c>Itemnum</c> do course (ja' no courses.txt
    /// como <c>item=ID,PROB</c>). As probabilidades nunca somam 100 — o que falta e' a
    /// hipotese de nao sair nada, que e' o "X" do ecra final.
    ///
    /// O item fica guardado em <see cref="_itemSorteado"/> para o <c>0x0089</c> o anunciar e
    /// entra ja' no inventario da conta. Ver Protocol.CoursePrize.
    /// </summary>
    private void SortearItemDoCourse()
    {
        _itemSorteado = 0;
        if (Account is null || Courses?.Get(_courseIndex) is not { } c ||
            c.Lotaria is not { Count: > 0 } lotaria) return;

        int dado = Random.Shared.Next(100);
        int acumulado = 0;
        foreach (var (item, prob) in lotaria)
        {
            acumulado += prob;
            if (dado < acumulado)
            {
                // A METADE ALTA DEPENDE DO TIPO DE ITEM, e nao e' sempre 2 — ver
                // Net.ItemTable.AltoDoCatalogo. Um avatar montado com 2 chega ao cliente como
                // uma seccao que nao existe, e a caixa do premio fica em branco.
                uint alto = Itens?.AltoDoCatalogo(item) ?? Protocol.CoursePrize.SeccaoAlta;
                _itemSorteado = (alto << 16) | (item & 0xFFFF);
                if (Itens is not null && Itens.Get(item) is null)
                    Log($"   (WARNING: item {item} from the draw is not in itens.txt; " +
                        $"sending it with section {alto})");
                break;
            }
        }

        if (_itemSorteado == 0)
        {
            Log($"   (draw for \"{c.Nome}\": roll {dado} in {acumulado}% — nothing)");
            return;
        }

        // A INSTANCIA E' A DATA. Ver Protocol.CoursePrize: os quatro itens ganhos no mesmo
        // dia na gravacao levam todos o mesmo numero.
        Account.Items.Add(new UserStore.Item
        {
            CatalogId = _itemSorteado,
            InstanceId = Protocol.CoursePrize.Data(DateTime.Now),
        });
        Store?.Save();
        Log($"   (draw for \"{c.Nome}\": roll {dado}, item {_itemSorteado} " +
            $"(0x{Protocol.CoursePrize.MetadeBaixa(_itemSorteado):x4}) into the inventory)");
    }

    private async Task SendAsync(ResponseMap.Message m, CancellationToken ct)
    {
        // AS SALAS DE OUTROS JOGADORES NAO EXISTEM AQUI, e o filtro tem de estar NESTE
        // sitio: ha' oito caminhos que chamam o SendAsync e so' um passava pela lista de
        // respostas onde o filtro estava antes. Por isso o lobby voltava a ter as salas
        // fantasma depois de um course — o fecho e a saida da sala vao por outro caminho.
        // Ver Protocol.RoomInfoUpdate.
        // O PREMIO SO' SE ANUNCIA SE TIVER SAIDO ALGUMA COISA. As duas gravacoes de course
        // ganharam item, por isso nao ha' amostra de "nao saiu nada"; a hipotese e' que o
        // servidor simplesmente nao manda o 0x0089, e o cliente mostra o "X" por omissao.
        if (!Replica && ItensDaConta && m.Id == Protocol.CoursePrize.MessageId && _itemSorteado == 0)
        {
            Log("   (prize: nothing came up, no 0x0089 sent)");
            return;
        }

        if (!Replica && !SalasGravadas && m.Id == Protocol.RoomInfoUpdate.MessageId)
        {
            Log("   (lobby: recorded room discarded)");
            return;
        }

        // O tamanho do pacote sai do corpo QUE VAI SER ENVIADO, nao do template.
        // O GameInfoInf tem tamanho variavel: o bloco da musica escolhida quase nunca
        // tem o mesmo tamanho do que estava na gravacao. Dimensionar pelo template
        // trunca o bloco e enche o resto de lixo â€” o cliente aceita-o, arranca a musica
        // e o video, mas comeca com o HP a zero e faz gameover imediato.
        // O modo music video vai no CABECALHO do StartInf, nao no corpo. O cliente pede-o
        // mas so' o respeita se o servidor confirmar; sem isto entra em gameplay.
        var header = m.Header;

        // O ecra de resultados diz A QUE MUSICA se refere, no cabecalho. Sem o marcar,
        // cada etapa de um course anuncia a musica que estava na gravacao.
        if (m.Id == Protocol.StageResult.ScreenId && _musicaEmCurso is { } emCurso &&
            Protocol.StageResult.PodeMarcarMusica(header) && !Replica)
        {
            ushort antes = BitConverter.ToUInt16(header, Protocol.StageResult.ScreenSongOffset);
            if (antes != (ushort)emCurso)
            {
                header = (byte[])header.Clone();
                Protocol.StageResult.MarcarMusica(header, emCurso);
                Log($"   (results: song {antes} -> {emCurso})");
            }
        }

        if (m.Id == StartInf && header.Length > Protocol.MusicVideo.ConfirmOffset)
        {
            byte want = _musicVideo ? (byte)1 : (byte)0;
            if (header[Protocol.MusicVideo.ConfirmOffset] != want)
            {
                header = (byte[])header.Clone();
                header[Protocol.MusicVideo.ConfirmOffset] = want;
                Log($"   (StartInf: mode {(_musicVideo ? "music video" : "normal")})");
            }
        }

        byte[] packet;
        if (m.Body.Length > 0)
        {
            var body = (byte[])m.Body.Clone();     // o template tem de ficar intacto

            // O 0x00C9 da rajada de arranque traz um registo PESSOAL DAQUELA MUSICA — nas
            // capturas do servidor real vem todo a 0xFF, que e' "sem registo".
            //
            // A gravacao `end_s1` tem-no preenchido (`88 01 00 56 8A 34 01 ...`), da musica
            // que quem gravou tinha jogado. Reenviar isso com outra musica fecha o cliente
            // durante o ecra de carregamento, sem mensagem nenhuma — foi o que se viu no
            // free mode e no course.
            //
            // Enche-se com 0xFF: e' o valor observado no servidor real nas duas capturas
            // (course2 e full_s1) e nao afirma nada de errado sobre a musica. Preencher com
            // o recorde verdadeiro exigia conhecer o formato, que ainda nao esta' decifrado.
            // A data do servidor. Sem isto o cliente e' informado de que hoje e' o dia em que
            // a gravacao foi feita — ver Protocol.LogInAck.
            if (m.Id == Protocol.LogInAck.MessageId && !Replica &&
                Protocol.LogInAck.CanPatch(body))
            {
                var (a, me, d, h, mi, s) = Protocol.LogInAck.Ler(body);
                Protocol.LogInAck.Escrever(body, DateTime.Now);
                Log($"   (server date: {a:0000}-{me:00}-{d:00} {h:00}:{mi:00}:{s:00} -> now)");
            }

            // A VELOCIDADE. O cliente escolhe-a e anuncia-a no 0x00C3; o servidor devolve-lha
            // no cabecalho do 0x00C4. Servir o 0x00C4 da gravacao impunha-lhe a velocidade de
            // quem gravou — x2 sempre em free mode, x1 na primeira etapa de qualquer course.
            // Ver Protocol.Velocidade.
            if (_velocidade is { } vel && m.Id == Protocol.Velocidade.MessageIdServidor &&
                !Replica && Protocol.Velocidade.PodeLer(header))
            {
                var (i0, s0) = Protocol.Velocidade.Ler(header);
                if (i0 != vel.Indice || s0 != vel.Scroll)
                {
                    header = (byte[])header.Clone();   // o template tem de ficar intacto
                    Protocol.Velocidade.Escrever(header, vel.Indice, vel.Scroll);
                    Log($"   (speed: {Protocol.Velocidade.Nome(i0)} -> " +
                        $"{Protocol.Velocidade.Nome(vel.Indice)} (index {vel.Indice}, scroll {vel.Scroll}))");
                }
            }

            // OS EFFECTORES VAO NO CORPO DO 0x00C4, e e' ai' que se perdiam. Ver
            // Protocol.Effectores: o servidor real devolve os 20 primeiros bytes do corpo do
            // 0x00C3 tal e qual, e nos servimos o corpo gravado — que os tem todos a zero.
            if (_effectores is { } efe && m.Id == Protocol.Velocidade.MessageIdServidor &&
                !Replica && Protocol.Effectores.PodeEscrever(body) &&
                !Protocol.Effectores.Iguais(body, efe))
            {
                Protocol.Effectores.Escrever(body, efe);
                Log($"   (effectors echoed back: {Protocol.Effectores.Descrever(efe)})");
            }

            // Em course mode nao ha' registo pessoal. Ver Protocol.PersonalRecord.
            if (_courseRoom && m.Id == Protocol.PersonalRecord.MessageId && !Replica &&
                Protocol.PersonalRecord.CanPatch(header) &&
                !Protocol.PersonalRecord.JaVazio(header, body))
            {
                header = (byte[])header.Clone();       // o template tem de ficar intacto
                Protocol.PersonalRecord.Limpar(header, body);
                Log("   (0x00c9: free mode personal record cleared; in a course it goes empty)");
            }

            // O 0x00CA leva o total corrido do course. Ver Protocol.StartParameter.
            if (_courseRoom && m.Id == Protocol.StartParameter.MessageId && !Replica &&
                Protocol.StartParameter.CanPatch(body))
            {
                int antes = Protocol.StartParameter.Ler(body);
                if (antes != _totalCorridoCourse)
                {
                    Protocol.StartParameter.Escrever(body, _totalCorridoCourse);
                    Log($"   (0x00ca: running course total {antes} -> {_totalCorridoCourse})");
                }
            }

            // MODO REPLICA: nao se toca em nada, sai exatamente o que foi gravado.
            //
            // Serve para responder a uma pergunta que as reescritas parciais nao deixam
            // responder: "o cliente comporta-se de forma diferente porque eu mudei alguma
            // coisa, ou apesar de eu nao ter mudado nada?". Com o perfil, o avatar, o
            // inventario e o nome todos reescritos por regras diferentes, uma sessao nunca
            // e' igual a' gravada nem quando parece ser — e as diferencas que sobram
            // confundem-se com ruido de sessao.
            //
            // Nao serve para jogar: a conta que aparece e' a de quem gravou.
            if (Replica)
            {
                _outgoing?.Encrypt(body);
                packet = new byte[header.Length + body.Length];
                header.CopyTo(packet, 0);
                body.CopyTo(packet, header.Length);
                await _stream.WriteAsync(packet, ct);
                Log($"   -> 0x{m.Id:x4} {packet.Length}B (replica)");
                return;
            }

            // O ID DO JOGADOR vem do login e serve a tabela de courses. E' o numero que o
            // cliente passa a ter como seu; nao se inventa, le'-se o que a gravacao anuncia.
            if (m.Id == Protocol.UserInfo.MessageId && Protocol.UserInfo.LerId(header) is var lido && lido != 0)
            {
                if (_userId != lido) Log($"   (player id: {lido})");
                _userId = lido;
            }

            // O SINALIZADOR DO ECRA DE BOAS-VINDAS. Vale 1 enquanto a conta nao tiver
            // nickname; e' o que faz o cliente pedir nickname, idade e sexo. Ver
            // Protocol.Credentials.AckPrimeiroLoginOffset e Protocol.BoasVindas.
            if (!Replica && m.Id == Protocol.Credentials.AckMessageId && Account is not null &&
                body.Length > Protocol.Credentials.AckPrimeiroLoginOffset)
            {
                // O NIVEL TAMBEM VAI AQUI, e ia o da gravacao. Na `conta_nova_s0` o +4 vale 1
                // e na `end_s0` vale 8 — sao os niveis das duas contas. Servir o 8 a uma conta
                // acabada de criar era anunciar-lhe um nivel que ela nao tem, e e' o candidato
                // mais forte a explicar porque' e' que o ecra de boas-vindas nao aparecia so'
                // com o sinalizador.
                if (body.Length >= Protocol.Credentials.AckLevelOffset + 4)
                    BitConverter.TryWriteBytes(
                        body.AsSpan(Protocol.Credentials.AckLevelOffset, 4), (uint)Account.Level);

                bool primeiro = string.IsNullOrWhiteSpace(Account.Nickname);
                body[Protocol.Credentials.AckPrimeiroLoginOffset] = (byte)(primeiro ? 1 : 0);
                Log($"   (0x0010: level {Account.Level + 1}" +
                    (primeiro ? ", account with no nickname — the client should ask for the welcome screen" : "") + ")");
            }

            // Reescrever o perfil com o estado desta sessao, em vez de reenviar os
            // valores congelados da gravacao.
            if (!SemFecho && !SemPerfilFecho && _profile is not null && m.Id == Protocol.UserProperty.MessageId &&
                Protocol.UserProperty.CanPatch(body))
            {
                var (l, x, mx) = Protocol.UserProperty.Read(body);
                Protocol.UserProperty.Write(body, _profile.Level,
                    SemXp ? x : _profile.Xp, SemMax ? mx : _profile.Max);
                EscreverRecorde(body, Protocol.UserProperty.RecordeOffset);
                // E OS DOIS DO MODO RANKING, um por canal. E' o que a caixa `我的最高得分` do
                // ecra de fim de musica mostra quando se joga em ranking — o cliente le' o do
                // canal em que esta'. Ver Protocol.UserProperty.RecordeRanking7KOffset.
                if (Account is not null)
                {
                    EscreverRecorde(body, Protocol.UserProperty.RecordeRankingOffset, Account.RankingScore);
                    EscreverRecorde(body, Protocol.UserProperty.RecordeRanking7KOffset, Account.RankingScore7K);

                    // E O QUE O PAINEL MOSTRA EM BAIXO. O cliente redesenha-o a partir DESTA
                    // mensagem, nao do 0x0043 do login — sem isto uma conta nova voltava aos
                    // numeros de quem gravou assim que acabava uma musica.
                    if (body.Length >= Protocol.UserProperty.CreditosOffset + 4)
                        BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserProperty.CreditosOffset, 4),
                                                   (uint)Math.Max(0, Account.Creditos));
                    if (body.Length >= Protocol.UserProperty.MaxComboOffset + 4)
                        BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserProperty.MaxComboOffset, 4),
                                                   (uint)Math.Max(0, Account.MaxCombo));
                    if (body.Length >= Protocol.UserProperty.MelhorPrecisaoOffset + 4)
                        BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserProperty.MelhorPrecisaoOffset, 4),
                                                   (uint)Math.Round(Account.MelhorPrecisao * 100));
                    if (body.Length >= Protocol.UserProperty.PrecisaoMediaOffset + 2)
                        BitConverter.TryWriteBytes(body.AsSpan(Protocol.UserProperty.PrecisaoMediaOffset, 2),
                                                   (ushort)Math.Round(Account.PrecisaoMedia * 100));
                }
                Log($"   (profile rewritten: level {l}->{_profile.Level}, " +
                    $"xp {x}->{_profile.Xp}, max {mx}->{_profile.Max})");
            }

            // Ecra de resultados: passar os campos que o cliente reportou nesta jogada.
            // O combo e o MAX ganho ficam reais; MAX, BREAK, pontuacao, bonus e precisao
            // continuam os da gravacao, porque o cliente nao os envia e o servidor real
            // calcula-os a partir de dados que ainda nao foram decifrados.
            if (!SemFecho && !SemResultados && _lastReport is not null && m.Id == Protocol.StageResult.ScreenId &&
                Protocol.StageResult.CanPatchScreen(body))
            {
                var (combo, max, brk) = Protocol.StageResult.Apply(body, _lastReport, _breaks, _precisao, PlayerProfile.XpDaJogada(_precisao, _breaks), _effectores, _modoVelocidade);
                Log($"   (results: MAX {max}, BREAK {brk}, combo {combo}, accuracy {_precisao:F2}%)");

                // SUBIU DE NIVEL: um byte no ecra de resultado, e e' ele que traz o pop-up.
                // Ver Protocol.StageResult.ScreenSubiuNivel — o servidor real nao manda
                // mensagem nenhuma para isto.
                if (body.Length > Protocol.StageResult.ScreenSubiuNivel)
                {
                    body[Protocol.StageResult.ScreenSubiuNivel] = (byte)(_subiuDeNivel ? 1 : 0);
                    if (_subiuDeNivel && _profile is not null)
                        Log($"   (levelled up to {_profile.Level + 1}: +{Protocol.StageResult.ScreenSubiuNivel}=1 on the screen)");
                }
                _subiuDeNivel = false;

                // NOVO RECORDE: a faixa "NEW RECORD!" por cima do numero. Vem de um byte, tal
                // como a subida de nivel. Ver Protocol.StageResult.ScreenNovoRecorde.
                //
                // Quem decide e' o ActualizarRecordeDeFreeMode, la' atras na mesma rajada — a
                // conta e' feita antes de o 0x0025 sair, porque e' esse que leva o numero do
                // recorde ao painel. Aqui so' se marca o resultado dela.
                //
                // Era replicado da gravacao, o que dava a faixa a quem nao tinha batido nada
                // sempre que calhasse a apanhar o fecho de uma musica que a tinha.
                if (body.Length > Protocol.StageResult.ScreenNovoRecorde)
                {
                    body[Protocol.StageResult.ScreenNovoRecorde] = (byte)(_novoRecorde ? 1 : 0);
                    if (_novoRecorde)
                        Log($"   (new record: +{Protocol.StageResult.ScreenNovoRecorde}=1 on the screen)");
                }
                _novoRecorde = false;

                // O medidor no fim da etapa. E' o unico observavel que falta para saber se o
                // COURSE FAILED vem do gauge: as quedas pequenas deixaram de aparecer no log
                // quando se passou a exigir meio break para contar uma.
                Log($"   (client gauge at the end of the stage: {(_hp is { } h ? h.ToString("F2") : "sem leitura")}" +
                    $"; all combo {(brk == 0 ? "yes" : "no")})");
                // O ECRA MOSTRA O QUE FOI MESMO CONCEDIDO. Campos +37 (XP) e +39 (MAX).
                //
                // Ate' aqui vinham da GRAVACAO — o XP e o MAX de quem a fez — e o servidor
                // somava a' conta um valor diferente, com os bonus dos itens equipados por
                // cima: o jogador via 19 e recebia 52, sem maneira de saber que a gear estava
                // a fazer alguma coisa.
                //
                // TEM DE ESTAR FORA DO `if (_courseRoom)`, que e' onde esteve na primeira
                // tentativa — assim so' valia no course, e o free mode continuava a mostrar
                // os numeros da gravacao.
                //
                // Isto AFASTA-SE da gravacao de proposito, e e' um afastamento que ela nao
                // contradiz: o print do servidor real mostra a soma limpa das etapas, mas nao
                // se sabe se quem gravou tinha gear equipada — se nao tinha, o valor cru e o
                // final sao o mesmo numero e a captura nao distingue os dois.
                int maxDoEcra = _maxConcedido ?? 0;   // locais: os campos limpam-se logo abaixo
                int xpDoEcra = _xpConcedido ?? 0;
                if (_maxConcedido is { } maxDado && _xpConcedido is { } xpDado &&
                    body.Length >= Protocol.StageResult.ScreenMaxGain + 2)
                {
                    BitConverter.TryWriteBytes(body.AsSpan(Protocol.StageResult.ScreenXpGain, 2),
                                               (ushort)Math.Clamp(xpDado, 0, ushort.MaxValue));
                    BitConverter.TryWriteBytes(body.AsSpan(Protocol.StageResult.ScreenMaxGain, 2),
                                               (ushort)Math.Clamp(maxDado, 0, ushort.MaxValue));
                    Log($"   (result screen: +{xpDado} XP, +{maxDado} MAX " +
                        "— what was actually granted)");
                }
                _xpConcedido = _maxConcedido = null;   // consumidos por este ecra

                // O total que acabou de ir para o ecra e' o que a rajada de arranque da
                // etapa seguinte tem de repetir no 0x00CA.
                if (_courseRoom)
                {
                    _totalCorridoCourse = BitConverter.ToUInt16(body, Protocol.StageResult.ScreenEndCombo);
                    _breaksCourse += brk;

                    // AS RESTANTES COLUNAS DO TOTAL RESULT. Le-se o que esta etapa acabou de
                    // pôr no ecra — o `Apply` ja' escreveu tudo — e soma-se. Sem isto o ecra
                    // COURSE SUCCESS mostrava os numeros da ULTIMA MUSICA e nao os do course:
                    // a pontuacao de uma musica so' onde devia estar a soma das tres.
                    //
                    // Uma etapa FALHADA nao entra: ou o jogador continua (e a repeticao conta
                    // no lugar dela) ou o course acaba ali e nao ha' ecra de sucesso.
                    if (!_etapaFalhada && body.Length >= Protocol.StageResult.ScreenAccuracy + 4)
                    {
                        _notasCourse += BitConverter.ToUInt16(body, Protocol.StageResult.ScreenMax);
                        _pontuacaoCourse += (int)BitConverter.ToUInt32(body, Protocol.StageResult.ScreenBaseScore);
                        _bonusCourse += (int)BitConverter.ToUInt32(body, Protocol.StageResult.ScreenBonus);
                        _precisoesCourse.Add(BitConverter.ToSingle(body, Protocol.StageResult.ScreenAccuracy));
                    }

                    // ETAPA FALHADA: barra a zero e +43=1. Servir os valores de uma etapa
                    // passada (100.0 e 02) fechava o cliente logo a seguir ao "continue".
                    // Ver Protocol.StageResult.MarcarEtapaFalhada.
                    if (_etapaFalhada)
                    {
                        Protocol.StageResult.MarcarEtapaFalhada(body);
                        Log("   (stage failed: +18=0.0, +43=1)");
                    }

                    // Ultima etapa: marcas de fim e numeros do COURSE INTEIRO.
                    else if (_fimDeCourse)
                    {
                        var cursoFim = Courses?.Get(_courseIndex);
                        Protocol.StageResult.MarcarFimDeCourse(body, cursoFim?.Preco ?? 0,
                                                               passou: !_courseFalhado);
                        BitConverter.TryWriteBytes(body.AsSpan(Protocol.StageResult.ScreenBreak, 2),
                                                   (ushort)Math.Clamp(_breaksCourse, 0, ushort.MaxValue));

                        // O TOTAL RESULT E' DO COURSE INTEIRO. A precisao e' media SIMPLES das
                        // etapas, e nao pesada pelas notas — ver MarcarTotaisDoCourse, que traz
                        // a medicao que separa as duas.
                        float precisaoCourse = _precisoesCourse.Count > 0
                            ? _precisoesCourse.Sum() / _precisoesCourse.Count
                            : 0f;
                        Protocol.StageResult.MarcarTotaisDoCourse(body, _notasCourse, _pontuacaoCourse,
                                                                  _bonusCourse, precisaoCourse);

                        Log(_courseFalhado
                            ? "   (end of course: COURSE FAILED — +1=1, +3=0xFF, +43=2)"
                            : $"   (end of course: COURSE SUCCESS — +1=1, +3={cursoFim?.Preco ?? 0} (price), +43=3)");
                        Log($"   (Total Result: BREAK {_breaksCourse}, notes {_notasCourse}, " +
                            $"score {_pontuacaoCourse}+{_bonusCourse}={_pontuacaoCourse + _bonusCourse}, " +
                            $"accuracy {precisaoCourse:F2}% (average of {_precisoesCourse.Count} stage(s)))");

                        // O veredicto ja' foi decidido no fecho da etapa — ver DecidirCourse.

                        // E O RESULTADO ENTRA NA TABELA DO COURSE. Guarda-se o melhor de
                        // sempre; e' isto que o 0x0084 passa a mostrar em vez da tabela
                        // gravada do servidor publico. Ver Protocol.CourseRank.
                        if (Account is not null &&
                            body.Length >= Protocol.StageResult.ScreenBonus + 4)
                        {
                            int score = (int)BitConverter.ToUInt32(body, Protocol.StageResult.ScreenBaseScore) +
                                        (int)BitConverter.ToUInt32(body, Protocol.StageResult.ScreenBonus);
                            int cmb = BitConverter.ToUInt16(body, Protocol.StageResult.ScreenComboAgain);
                            string k = ChaveDoCourse(_courseIndex);
                            int antes = 0;
                            if (Account.CourseScores.TryGetValue(k, out var guard) &&
                                int.TryParse(guard.Split(',')[0], out int a)) antes = a;
                            if (score > antes)
                            {
                                // A data fica gravada com a pontuacao: a tabela mostra-a
                                // (ver Protocol.CourseRank, campo +37).
                                Account.CourseScores[k] =
                                    $"{score},{cmb},{Protocol.CourseRank.DataDeHoje(DateTime.Now)}";
                                Store?.Save();
                                Log($"   (ranking {k}: new record {score} (was {antes}), combo {cmb})");
                            }
                            else Log($"   (ranking {k}: {score} does not beat the record {antes})");
                        }
                        _fimDeCourse = false;
                    }

                    // O MAX DO "TOTAL RESULT" E' O DO COURSE INTEIRO.
                    //
                    // O ecra final le' os seus numeros do ULTIMO 0x0070. Os outros campos ja'
                    // vem somados porque e' o proprio cliente que os acumula (COMBO 571 =
                    // 139+128+304 na sessao de teste); o MAX ganho nao, porque esse sou eu que
                    // o calculo por etapa. Sem isto o ecra final mostrava so' o ganho da ultima
                    // musica (13 em vez de 35).
                    //
                    // Acumula-se o valor CONCEDIDO, nao o que o cliente reportou, pela mesma
                    // razao de cima.
                    _maxGanhoCourse += maxDoEcra;
                    _xpGanhoCourse += xpDoEcra;

                    var curso = Courses?.Get(_courseIndex);
                    if (curso is { } c && _courseSong >= c.Musicas.Count &&
                        body.Length >= Protocol.StageResult.ScreenMaxGain + 2)
                    {
                        int totalDoCourse = _maxGanhoCourse;

                        // O BONUS DE CONCLUSAO — o "50% MAX 加成" que o ecra de escolha anuncia
                        // (campo `Max` do CourseSection.ini, ja' no courses.txt como `max=`).
                        // Aplica-se sobre o MAX ganho no course inteiro, e ENTRA NO TOTAL que o
                        // ecra mostra: anuncia-lo na escolha e depois escondê-lo no fim era o
                        // mesmo problema dos bonus dos itens.
                        // UM COURSE FALHADO NAO LEVA BONUS DE CONCLUSAO. Medido na captura do
                        // "Fine Day" falhado: o MAX final ficou em 9, que e' a soma crua das
                        // etapas (2+4+3), sem o +30% que o course promete a quem passa.
                        if (_profile is not null && !_etapaFalhada && !_courseFalhado)
                        {
                            if (c.BonusMax > 0 && _maxGanhoCourse > 0)
                            {
                                int bonus = (int)Math.Round(_maxGanhoCourse * (c.BonusMax / 100.0));
                                if (bonus > 0)
                                {
                                    int antes = _profile.Max;
                                    _profile.Max += bonus;
                                    _profile.Save();
                                    totalDoCourse += bonus;
                                    Log($"   (completion bonus for \"{c.Nome}\": +{c.BonusMax}% of " +
                                        $"{_maxGanhoCourse} = +{bonus} MAX, {antes} -> {_profile.Max})");
                                }
                            }

                            // O IRMAO DO BONUS DE MAX: o campo `Exp` do CourseSection.ini, o
                            // `经验值 N%` do painel do COURSE SUCCESS. 29 dos 43 courses tem-no
                            // a zero — e' por isso que o "Let's Begin" mostra 0% —, e os
                            // outros 14 vao ate' 1500%.
                            //
                            // NAO vai para o ecra: o Total Result nao tem coluna de XP. O que
                            // o jogador ve' e' a barra de experiencia a andar.
                            if (c.BonusExp > 0 && _xpGanhoCourse > 0)
                            {
                                int bonus = (int)Math.Round(_xpGanhoCourse * (c.BonusExp / 100.0));
                                if (bonus > 0)
                                {
                                    bool subiu = _profile.GanharXp(bonus);
                                    if (subiu) _subiuDeNivel = true;
                                    Log($"   (completion bonus for \"{c.Nome}\": +{c.BonusExp}% of " +
                                        $"{_xpGanhoCourse} = +{bonus} XP, {_profile}" +
                                        (subiu ? "  *** LEVELLED UP ***" : "") + ")");
                                }
                            }
                        }

                        BitConverter.TryWriteBytes(body.AsSpan(Protocol.StageResult.ScreenMaxGain, 2),
                                                   (ushort)Math.Clamp(totalDoCourse, 0, ushort.MaxValue));
                        Log($"   (Total Result: MAX for the whole course = {totalDoCourse}" +
                            (totalDoCourse != _maxGanhoCourse ? $" ({_maxGanhoCourse} + bonus)" : "") + ")");
                    }
                }

                // O RECORDE PESSOAL E' SO' DO FREE MODE.
                //
                // O ecra de perfil chama-lhe "自由模式最高得分" — melhor resultado do modo
                // livre — e os resultados de course tem o seu proprio sitio, a tabela de cada
                // course (ver Protocol.CourseRank). Contar as etapas de um course aqui
                // misturava as duas coisas e enchia o recorde do perfil com pontuacoes que
                // nao lhe pertencem.
                if (Account is not null && !_courseRoom &&
                    body.Length >= Protocol.StageResult.ScreenBonus + 4)
                {
                    int fim = (int)BitConverter.ToUInt32(body, Protocol.StageResult.ScreenBaseScore)
                            + (int)BitConverter.ToUInt32(body, Protocol.StageResult.ScreenBonus);

                    // UMA MUSICA PERDIDA NAO DEIXA RECORDE. O jogador vai abaixo, volta a' lista
                    // e nao devia levar nada dali — mas isto nao tinha guarda nenhuma, ao
                    // contrario do XP e do MAX. Numa conta NOVA notava-se: o primeiro game over
                    // escrevia como recorde pessoal a pontuacao que o ecra tivesse calculado,
                    // por ser maior que o zero de partida.
                    if (_etapaFalhada)
                        Log($"   (song failed: {fim} does not count towards the record)");

                    // POR CANAL. O painel de perfil tem uma caixa para cada um e sao numeros
                    // independentes — ver Protocol.UserInfo.BestScore7KOffset.
                    else if (fim > Account.RecordeDoCanal(Canal))
                    {
                        int antes = Account.RecordeDoCanal(Canal);
                        Account.PorRecordeDoCanal(Canal, fim);
                        Store?.Save();
                        Log($"   (free mode record on {Canal.ToUpperInvariant()}: {fim} (was {antes}))");
                    }
                }
            }

            // O avatar viaja em DUAS mensagens e as duas tem de ser reescritas:
            //   0x003C WaiterInfoUpdateInf +51  -> a entrada na lista de jogadores
            //   0x0043 UserInfoInf         +53  -> o painel de perfil do lobby
            //
            // Corrigi primeiro so' o 0x003C e o perfil continuou com a mascara vermelha de
            // quem gravou, o que me fez procurar o erro na logica de escolha do avatar
            // durante tres rondas. O log ate' dizia que estava a escrever o valor certo —
            // estava, mas na mensagem que nao era a que se ve'. Confirmado em cinco
            // capturas: os dois campos levam sempre o mesmo valor.
            // A entrada do jogador na lista do lobby repete o indice que o ConnectAck ja'
            // anunciou. Se a rajada de login vier de outra gravacao (--login-de), os dois
            // deixam de bater — e quem manda e' o ConnectAck, que o cliente ja' leu.
            // OUTROS JOGADORES NAO EXISTEM AQUI. As gravacoes foram feitas no servidor
            // publico, e trazem entradas de lista de quem la' estava — era assim que o
            // "Gaejonmot" aparecia no lobby. Reescrever indice, nivel e avatar nao chegava:
            // o NOME passava intacto.
            if (m.Id == Protocol.WaiterInfo.MessageId && Account is not null &&
                Protocol.WaiterInfo.CanPatch(body))
            {
                string quem = Protocol.WaiterInfo.ReadName(body);
                if (!string.Equals(quem, Account.Name, StringComparison.Ordinal))
                {
                    Log("   (lobby: entries cleared)");
                    return;
                }
            }

            if (m.Id == Protocol.WaiterInfo.MessageId && Protocol.WaiterInfo.CanPatch(body))
            {
                ushort tinha = Protocol.WaiterInfo.ReadIndex(body);
                if (tinha != _waiterIndex)
                {
                    Protocol.WaiterInfo.WriteIndex(body, _waiterIndex);
                    Log($"   (list: player index {tinha}->{_waiterIndex})");
                }

                // O nivel na coluna 等级 da lista do lobby. E' um campo proprio: sem isto o
                // jogador aparecia a nivel 9 na lista e a 11 no painel de perfil.
                if (_profile is not null)
                {
                    int nivelTinha = Protocol.WaiterInfo.ReadLevel(body);
                    if (nivelTinha != _profile.Level)
                    {
                        Protocol.WaiterInfo.WriteLevel(body, _profile.Level);
                        Log($"   (list: level {nivelTinha}->{_profile.Level})");
                    }
                }
            }

            if (Account is not null &&
                ((m.Id == Protocol.WaiterInfo.MessageId && Protocol.WaiterInfo.CanPatch(body)) ||
                 (m.Id == Protocol.UserInfo.MessageId && body.Length >= Protocol.UserInfo.AvatarOffset + 2)))
            {
                bool naLista = m.Id == Protocol.WaiterInfo.MessageId;

                int offset = naLista ? Protocol.WaiterInfo.AvatarOffset : Protocol.UserInfo.AvatarOffset;
                ushort novo = AvatarDaConta(out var deOnde);
                ushort antes = BitConverter.ToUInt16(body, offset);
                BitConverter.TryWriteBytes(body.AsSpan(offset, 2), novo);

                // Nao apagar o avatar de omissao QUE A CONTA ESCOLHEU: so' se guarda 0 quando
                // o valor veio mesmo do recurso, e nao quando e' o do sexo do jogador.
                ushort guardar = novo == Protocol.WaiterInfo.AvatarPorOmissao &&
                                 Account.Avatar != Protocol.UserInfo.AvatarMasculino
                    ? (ushort)0 : novo;
                if (Account.Avatar != guardar) { Account.Avatar = guardar; Store?.Save(); }

                Log($"   ({(naLista ? "list" : "profile")}: avatar 0x{antes:x4} -> 0x{novo:x4} ({deOnde})" +
                    (naLista ? $", index {_waiterIndex}" : ""));
            }

            // Inventario da conta. Os offsets sao os medidos na captura inv2 (ver
            // InventoryCodec): possuidos em +316, equipados em +676. A tentativa anterior
            // escrevia em +308, oito bytes cedo, o que punha os itens no separador de
            // eventos e fazia o cliente anunciar prendas por levantar.
            //
            // A seccao da coleccao (0..315) fica como esta' na gravacao: sao os discos, que
            // nao passam pela loja.
            // --inv-gravado: nao mexer no inventario, deixar o da gravacao.
            //
            // O course mode so' arranca depois de se ter passado por uma sala de free mode, e
            // o inventario e' das poucas coisas que eu reescrevo e que a sala de free mode
            // faria o cliente reconstruir. Nas quatro gravacoes o jogador TEM itens; eu
            // mando-lhe sempre "0 itens, 0 equipados", e o ecra de jogo tem quatro FX ITEM
            // SLOT que o course configura pelos campos EffectA..D do CourseSection.ini.
            if (InventarioGravado && m.Id == InventoryInfoInf)
                Log("   (--inv-gravado: inventory exactly as recorded)");
            else if (Account is not null && m.Id == InventoryInfoInf &&
                body.Length >= Protocol.InventoryCodec.MountLimit)
            {
                // A seccao de coleccao (0..315) vem da gravacao indicada em
                // Config.GravacaoColeccao, e nao da principal: a `end_s1` tem uma categoria
                // a menos que as sessoes onde o course mode arranca. Ver a nota la'.
                var fonteColeccao = new[] { _map }.Concat(Extras)
                    .FirstOrDefault(f => f.Nome == Config.GravacaoColeccao);
                var modelo = fonteColeccao?.FindSetContaining(InventoryInfoInf)
                                          .FirstOrDefault(x => x.Id == InventoryInfoInf);
                if (modelo is { Body.Length: >= Protocol.InventoryCodec.OwnedOffset } &&
                    !modelo.Value.Body.AsSpan(0, Protocol.InventoryCodec.OwnedOffset)
                           .SequenceEqual(body.AsSpan(0, Protocol.InventoryCodec.OwnedOffset)))
                {
                    modelo.Value.Body.AsSpan(0, Protocol.InventoryCodec.OwnedOffset)
                          .CopyTo(body.AsSpan(0, Protocol.InventoryCodec.OwnedOffset));
                    Log($"   (collection: section 0..315 from {Config.GravacaoColeccao})");
                }

                // A COLECCAO PASSA A SER DA CONTA. A seccao 0..315 e' a lista de discos (ver
                // Protocol.DefaultItems). Vinda da gravacao nunca mexia, e por isso os discos
                // ganhos no course nao ficavam registados e o premio de fim de etapa saia "X"
                // — o cliente mostra a DIFERENCA, e nao havia diferenca.
                //
                // O PRIMEIRO PAR DA LISTA VEM NO CABECALHO, nao no corpo. Lendo so' o corpo,
                // o 0x0406 (dourado) nunca entrava na conta: ficava preso no numero da
                // gravacao e desaparecia assim que a lista da conta era escrita.
                var doTemplate = Protocol.DefaultItems.LerDoLogin(header, body);

                // Semeia-se a ZERO o que a conta ainda nao tem. A quantidade vinha daqui ate'
                // 14/08/2026, e era a de quem gravou: uma conta acabada de criar herdava a
                // coleccao inteira do MDashK.
                //
                // O QUE ISTO SEMEIA E' A LISTA DE CATEGORIAS, NAO O QUE VAI PARA A LINHA. Os
                // zeros ficam no ficheiro da conta e servem de saco de onde o premio de fim de
                // etapa e' sorteado (ver PremiarItemDeEtapa); para a linha nao vao — quem os
                // filtra e' o DefaultItems.Ordenar, e e' por isso que um disco por descobrir
                // aparece com "?" em vez de "0".
                int semeados = 0;
                foreach (var (id, _) in doTemplate)
                    if (!Account.DefaultItems.ContainsKey(id.ToString()))
                    {
                        Account.DefaultItems[id.ToString()] = 0;
                        semeados++;
                    }
                if (semeados > 0)
                {
                    Store?.Save();
                    Log($"   (collection: {semeados} category(ies) seeded at zero)");
                }

                if (ItensDaConta)
                {
                    header = (byte[])header.Clone();
                    var contagens = ContagensDaConta();
                    var lista = Protocol.DefaultItems.Ordenar(doTemplate.Select(t => t.Id), contagens);
                    Protocol.DefaultItems.EscreverNoLogin(header, body, lista,
                        Protocol.InventoryCodec.OwnedOffset / Protocol.DefaultItems.EntrySize);
                    int porDescobrir = contagens.Count - lista.Count;
                    Log($"   (collection: {lista.Count} disc(s) on the account, the 1st in the header" +
                        (porDescobrir > 0 ? $"; {porDescobrir} undiscovered, sent as \"?\")" : ")"));
                }

                int p = Protocol.InventoryCodec.WriteItems(body, Protocol.InventoryCodec.OwnedOffset,
                    Account.Items.Select(i => (i.CatalogId, i.InstanceId)),
                    Protocol.InventoryCodec.OwnedLimit);
                int e = Protocol.InventoryCodec.WriteItems(body, Protocol.InventoryCodec.MountOffset,
                    Account.Items.Where(i => i.Equipped).Select(i => (i.CatalogId, i.InstanceId)),
                    Protocol.InventoryCodec.MountLimit);
                Log($"   (inventory of {Account.Name}: {p} item(s), {e} equipped)");
            }

            // O nome de quem gravou a sessao esta' escrito dentro das mensagens; sem
            // trocar, qualquer conta aparece no jogo com esse nome.
            if (Names is not null && !Names.IsNoOp)
            {
                int trocas = Names.Apply(body);
                if (trocas > 0)
                    Log($"   (recording: {trocas}x \"{Names.From}\" written into the body -> \"{Names.To}\")");
            }

            // O NICKNAME NO 0x0010, E TEM DE SER DEPOIS DA TROCA DE NOMES — o NameRewriter
            // passa por este mesmo campo e apagaria o que aqui se escrevesse antes.
            //
            // Sem nickname vai um til e o utilizador; e' isso que abre o ecra de boas-vindas.
            // Ver Protocol.Credentials.AckNicknameOffset.
            if (!Replica && m.Id == Protocol.Credentials.AckMessageId && Account is not null &&
                body.Length >= Protocol.Credentials.AckNicknameOffset +
                               Protocol.Credentials.AckNicknameTamanho)
            {
                bool semNick = string.IsNullOrWhiteSpace(Account.Nickname);
                var texto = semNick
                    ? (char)Protocol.Credentials.SemNickname + Account.Name
                    : Account.Nickname;
                var bytes = System.Text.Encoding.ASCII.GetBytes(texto);

                // sem Span: este metodo e' async e o compilador nao a deixa atravessar um await
                Array.Clear(body, Protocol.Credentials.AckNicknameOffset,
                            Protocol.Credentials.AckNicknameTamanho);
                Array.Copy(bytes, 0, body, Protocol.Credentials.AckNicknameOffset,
                           Math.Min(bytes.Length, Protocol.Credentials.AckNicknameTamanho));
                Log($"   (0x0010 nickname: \"{texto}\"" +
                    (semNick ? " — the welcome box will open" : "") + ")");
            }

            // DIAGNOSTICO: forca o +2 do bloco em TODAS as musicas, free mode incluido.
            //
            // Foi a sonda que ELIMINOU o +2 como candidato a velocidade: forcado a 20 e a 40,
            // o jogo nao mudou nada, logo o cliente nao le' este campo. (E as velocidades
            // vao de 0.5 em 0.5, o que ja' excluia os valores 13 e 14 que la' aparecem.)
            // Fica como sonda — o que o +2 e' continua por saber. Ver docs/por-fazer.md.
            if (Velocidade is { } v && m.Id == GameInfoFraming.MessageId &&
                body.Length > GameInfoFraming.SequenciaOffset)
            {
                byte antes = body[GameInfoFraming.SequenciaOffset];
                body[GameInfoFraming.SequenciaOffset] = v;
                Log($"   (block: +{GameInfoFraming.SequenciaOffset} {antes} -> {v})");
            }

            // A TABELA DE HIGH SCORES DE CADA COURSE E' CONSTRUIDA AQUI, do zero.
            //
            // As gravacoes so' tem tabela para os 21 courses percorridos na course_s1; para
            // os outros ia a de outro course, e a navegar na lista a tabela saltava. E as que
            // ha' trazem jogadores reais do servidor publico (CHN_L7, ccs00258, Splash), que
            // nao ha' razao nenhuma para levar num servidor proprio.
            //
            // O CABECALHO TAMBEM TEM DE SER REESCRITO, e nao era: em [3..4] vai o course e em
            // [5..6] o ID DO JOGADOR DO PRIMEIRO LUGAR (ver Protocol.CourseRank). Aproveitando
            // o template de outro course, saia' um corpo com a pontuacao desta conta debaixo
            // do id de outra pessoa — ou, ao contrario, um corpo com jogadores e o id a zero,
            // que e' um lugar 1 que nao existe. Nos dois casos o cliente ficava com a tabela
            // que ja' tinha no ecra, e era isso o "arrasto" de um course para o outro.
            //
            // O "YOUR RANK" NAO E' UM CAMPO DESTA MENSAGEM: e' o cliente que se coloca na
            // tabela sozinho. Com a tabela vazia ele fica em primeiro porque nao ha' ninguem
            // acima; nao ha' aqui nada que o servidor possa escrever para dizer "sem
            // classificacao".
            if (!Replica && m.Id == CourseRankAck && body.Length > 0)
            {
                var tabela = TabelaDoCourse(_rankCourse, Protocol.CourseRank.Lugares(body.Length));
                ushort idPrimeiro = Protocol.CourseRank.Escrever(body, tabela);
                if (header.Length >= Protocol.CourseRank.HeaderIdOffset + 2)
                {
                    header = (byte[])header.Clone();
                    if (_rankCourse >= 0)
                        BitConverter.TryWriteBytes(
                            header.AsSpan(Protocol.CourseRank.HeaderCourseOffset, 2), (ushort)_rankCourse);
                    BitConverter.TryWriteBytes(
                        header.AsSpan(Protocol.CourseRank.HeaderIdOffset, 2), idPrimeiro);
                }
                Log(tabela.Count > 0
                    ? $"   (ranking {ChaveDoCourse(_rankCourse)}: {tabela.Count} slot(s) — " +
                      $"{string.Join(", ", tabela.Take(3).Select(e => $"{e.Nome} {e.Score}"))}" +
                      $"{(tabela.Count > 3 ? ", ..." : "")}; id of the 1st {idPrimeiro})"
                    : $"   (ranking {ChaveDoCourse(_rankCourse)}: empty, id 0)");
            }

            // O PREMIO DE ITEM DO FIM DO COURSE. O cabecalho leva a metade baixa do id e a
            // quantidade; o corpo leva a data. Servido da gravacao, anunciava sempre o item
            // que quem gravou ganhou naquele dia. Ver Protocol.CoursePrize.
            if (!Replica && ItensDaConta && m.Id == Protocol.CoursePrize.MessageId &&
                _itemSorteado != 0 && Protocol.CoursePrize.PodeEscrever(header))
            {
                header = (byte[])header.Clone();
                Protocol.CoursePrize.Escrever(header, body, _itemSorteado, 1, DateTime.Now);
                Log($"   (course prize: item {_itemSorteado})");
            }

            // E O INVENTARIO QUE O CLIENTE VAI BUSCAR A SEGUIR (0x002C). Todo o corpo e' a
            // lista de itens possuidos; da gravacao, trazia os de quem gravou — era por isso
            // que o inventario mostrava coisas que a conta nao tinha e o premio nao aparecia.
            if (!Replica && ItensDaConta && Account is not null &&
                m.Id == Protocol.CoursePrize.InventarioId && body.Length >= Protocol.InventoryCodec.EntrySize)
            {
                int n = Protocol.InventoryCodec.WriteItems(body, 0,
                    Account.Items.Select(i => (i.CatalogId, i.InstanceId)), body.Length);
                Log($"   (inventory 0x002C: {n} item(s) on the account)");
            }

            // O 0x002A leva a MESMA lista, e aqui TODO o corpo e' dela — sem par nenhum no
            // cabecalho, ao contrario do 0x0044. Ver Protocol.DefaultItems.
            //
            // Esta reescrita chegou a estar desligada porque fazia desaparecer os discos
            // dourados a meio da sessao. A causa era a leitura do 0x0044: o par do dourado
            // viaja no cabecalho, nunca entrava na conta, e a lista da conta saia' com sete
            // pares onde o cliente esperava oito. Com o dourado na conta, ja' se pode
            // reescrever — e e' preciso, senao a coleccao so' se actualiza no login seguinte.
            if (ItensDaConta && Account is not null && m.Id == UpdInvDefaultItemInf &&
                body.Length >= Protocol.DefaultItems.EntrySize)
            {
                var contagens = ContagensDaConta();
                if (contagens.Count > 0)
                {
                    var lista = Protocol.DefaultItems.Ordenar(
                        Protocol.DefaultItems.LerLista(body).Select(t => t.Id), contagens);
                    Protocol.DefaultItems.EscreverLista(body, 0, lista,
                                                        body.Length / Protocol.DefaultItems.EntrySize);
                    int porDescobrir = contagens.Count - lista.Count;
                    Log($"   (collection 0x002A: {lista.Count} disc(s) on the account" +
                        (porDescobrir > 0 ? $"; {porDescobrir} undiscovered)" : ")"));
                }
            }

            // O ReadyInf fecha a musica e prepara a seguinte, e leva no CABECALHO o indice
            // do jogador e o byte baixo do saldo de MAX. A classe Protocol.ReadyInf estava
            // escrita e medida em quatro gravacoes, mas nunca chegou a ser usada: o 0x005E
            // saia com o indice e o saldo da gravacao.
            //
            // Medido no fecho do continue, mesmo course e mesma etapa:
            //   real  5E 00 7E | 00 17 01 | 49   -> indice 279, MAX baixo 0x49
            //   meu   5E 00 D1 | 00 53 00 | 2A   -> indice  83 (o da course2_s1!), 0x2A
            //
            // Um indice que contradiz o que o ConnectAck anunciou e' exactamente o tipo de
            // contradicao que congelava o course a' entrada.
            if (m.Id == Protocol.ReadyInf.MessageId && Protocol.ReadyInf.CanPatch(header))
            {
                var (i0, mx0) = Protocol.ReadyInf.Ler(header);
                int max = _profile?.Max ?? mx0;
                if (i0 != _waiterIndex || mx0 != (byte)(max & 0xFF))
                {
                    header = (byte[])header.Clone();
                    Protocol.ReadyInf.Escrever(header, _waiterIndex, max);
                    Log($"   (ReadyInf: index {i0}->{_waiterIndex}, low MAX {mx0}->{max & 0xFF})");
                }
            }

            // O DJ MESSENGER vazio. A gravacao traz o amigo "Evance" e sem isto ele aparecia
            // sempre na janela; este servidor nao tem lista de amigos nenhuma. Os dois
            // estados estao medidos contra uma sessao gravada de proposito ja' sem amigos —
            // ver Protocol.Messenger.
            if (m.Id == Protocol.Messenger.ListaId && Protocol.Messenger.TemAmigos(header))
            {
                header = Protocol.Messenger.ListaVazia(header);
                body = Array.Empty<byte>();
                Log("   (DJ Messenger: friend list emptied)");
            }
            else if (m.Id == Protocol.Messenger.InfoId && Protocol.Messenger.TemAmigos(header))
            {
                header = (byte[])header.Clone();
                Protocol.Messenger.Limpar(header, body);
                Log("   (DJ Messenger: recorded friend discarded)");
            }

            // O indice do jogador no lobby tem de ser o MESMO em todo o lado. O ConnectAck
            // ja' o anunciou (em claro, no cabecalho) e o 0x003c repete-o; se o 0x0051 vier
            // de outra gravacao, traz o indice de OUTRA sessao e o cliente fica a ouvir dois
            // numeros para o mesmo jogador.
            //
            // Era isto que travava o course mode quando se ia directamente para la': a
            // rajada da sala de course vem da course2_s1 (indice 83) enquanto o ConnectAck
            // vem da end_s1 (indice 270). Em free mode a rajada tambem vinha da end_s1, por
            // isso batia certo — dai' a impressao de que era preciso passar primeiro pelo
            // free mode. Ver Protocol.LogInAck.ConnectAckIndexOffset.
            if (m.Id == Protocol.UserRoomInfo.MessageId &&
                Protocol.UserRoomInfo.PodeEscreverIndice(body))
            {
                ushort tinha = Protocol.UserRoomInfo.LerIndice(body);
                if (tinha != _waiterIndex)
                {
                    Protocol.UserRoomInfo.EscreverIndice(body, _waiterIndex);
                    Log($"   (room: player index {tinha}->{_waiterIndex})");
                }
            }

            // Terceira mensagem com o perfil, enviada ao entrar na sala. Sem a reescrever,
            // o cliente recebe o perfil atualizado no login e logo a seguir esta com os
            // valores da gravacao — dois numeros diferentes para o mesmo jogador.
            if (_profile is not null && m.Id == Protocol.UserRoomInfo.MessageId &&
                Protocol.UserRoomInfo.CanPatch(body))
            {
                var (l, mx) = Protocol.UserRoomInfo.Read(body);
                Protocol.UserRoomInfo.Write(body, _profile.Level, SemMax ? mx : _profile.Max);
                EscreverRecorde(body, Protocol.UserRoomInfo.BestScoreOffset);
                Log($"   (room: level {l}->{_profile.Level}, max {mx}->{_profile.Max})");
            }

            // O ecra de perfil do login le' daqui. Sem reescrever esta, o progresso
            // parece perder-se sempre que o jogador volta a entrar.
            if (_profile is not null && m.Id == Protocol.UserInfo.MessageId &&
                Protocol.UserInfo.CanPatch(body))
            {
                var (l, x, mx) = Protocol.UserInfo.Read(body);
                Protocol.UserInfo.Write(body, _profile.Level,
                    SemXp ? x : _profile.Xp, SemMax ? mx : _profile.Max);
                EscreverOsDoisRecordes(body);
                EscreverPerfilDaConta(body);
                Log($"   (login: level {l}->{_profile.Level}, xp {x}->{_profile.Xp}, " +
                    $"max {mx}->{_profile.Max}, records {Account?.BestScore ?? 0} (5K) / " +
                    $"{Account?.BestScore7K ?? 0} (7K))");
            }

            // AS NOTIFICACOES EM INGLES. O texto delas nao esta' em ficheiro nenhum do jogo —
            // e' o servidor que manda a frase feita, e nos repetiamos os bytes chineses da
            // gravacao. Reescreve-se o corpo e corrige-se o comprimento, que neste id viaja no
            // cabecalho (ver MessageSizes.LengthInHeader). Um texto que nao seja de um molde
            // conhecido passa como esta'. Ver Protocol.Aviso.
            if (!Replica && m.Id == Protocol.Aviso.MessageId &&
                Protocol.Aviso.EmIngles(body, NomesDeItens.Traduzir) is { } traduzido)
            {
                body = traduzido;
                header = (byte[])header.Clone();
                BitConverter.TryWriteBytes(
                    header.AsSpan(Protocol.MessageSizes.HeaderLengthOffset, 4),
                    header.Length + body.Length);
                Log($"   (notice translated: \"{System.Text.Encoding.ASCII.GetString(body, 1, body.Length - 2)}\")");
            }

            _outgoing?.Encrypt(body);
            packet = new byte[header.Length + body.Length];
            header.CopyTo(packet, 0);
            body.CopyTo(packet, header.Length);
        }
        else
        {
            packet = (byte[])header.Clone();
        }
        await _stream.WriteAsync(packet, ct);

        // Marca-se aqui, e nao em quem monta a resposta, porque o 0x0086 que tem de esperar
        // por ele vem de OUTRO pedido (o 0x0085) — e a essa altura ja' ninguem sabe quando
        // saiu o ultimo ranking. Ver PausaDepoisDoRanking.
        if (m.Id == CourseRankAck) _ultimo0x0084 = DateTime.UtcNow;

        Log($"   -> 0x{m.Id:x4} {packet.Length}B");
    }

    /// <summary>Procura um conjunto de respostas na gravacao principal e nas suplementares.</summary>
    /// <summary>
    /// Repoe o saldo de MAX depois de uma compra ou venda, com um
    /// <c>UpdateUserPropertyInf</c> (0x0025).
    ///
    /// PORQUE: o saldo que viaja no ack de compra/venda esta' no cabecalho e tem so' 16
    /// bits, logo satura nos 65535. O cliente confia nele e passa a impedir compras acima
    /// desse valor — "您的MAX不够" — mesmo tendo a conta muito mais. O 0x0025 leva o MAX em
    /// 32 bits, por isso reenvia-lo devolve o numero certo ao ecra.
    ///
    /// O servidor real NAO faz isto: nas capturas o ack vem sozinho. E' um desvio
    /// deliberado, do lado seguro — o 0x0025 e' a mensagem normal de atualizacao de perfil,
    /// que o cliente ja' recebe no fim de cada musica.
    /// </summary>
    private async Task ReporSaldoAsync(CancellationToken ct)
    {
        if (_profile is null || _profile.Max <= ushort.MaxValue) return;

        var prop = FindAnywhere(Protocol.UserProperty.MessageId)
                       .FirstOrDefault(x => x.Id == Protocol.UserProperty.MessageId);
        if (prop.Body is null || !Protocol.UserProperty.CanPatch(prop.Body)) return;

        var corpo = (byte[])prop.Body.Clone();
        Protocol.UserProperty.Write(corpo, _profile.Level, _profile.Xp, _profile.Max);
        await SendAsync(new ResponseMap.Message(prop.Id, prop.Header, corpo), ct);
        Log($"   (balance restored in 32 bits: {_profile.Max} MAX)");
    }

    /// <summary>
    /// Qual o avatar a mostrar, nos 16 bits baixos do id de catalogo.
    ///
    /// Preferencia ao que o cliente anunciou no <c>0x0023</c>, desde que o item continue
    /// equipado — ele so' o envia quando lhe apetece, e trocar de avatar sem passar por ai'
    /// deixava o valor preso no anterior. Em recurso, o item equipado que pareca um avatar:
    /// a categoria le-se pela gama do catalogo (avatares 99332..101382, gear
    /// 107545..107556, notas 108056..108574), que e' a parte menos solida disto. Sem nada
    /// equipado devolve o avatar por omissao — deixar o campo como esta' na gravacao punha
    /// la' a mascara vermelha de quem gravou.
    /// </summary>
    private ushort AvatarDaConta(out string origem)
    {
        var itens = Account?.Items ?? new List<UserStore.Item>();

        // OS DOIS AVATARES DE OMISSAO NAO SAO ITENS, e por isso nao se lhes pode exigir um
        // item equipado. Sao os que o jogo da' conforme o sexo escolhido no ecra de
        // boas-vindas — 0xF001 feminino, 0xF002 masculino — e uma conta nova nao tem avatar
        // nenhum no inventario.
        //
        // Sem esta linha o teste seguinte falhava, caia-se no "por omissao" (que e' o
        // MASCULINO) e, pior, o bloco que guarda o valor escrevia 0 por cima: uma conta
        // feminina perdia o avatar no primeiro login e voltava a azul.
        if (Account is not null &&
            (Account.Avatar == Protocol.UserInfo.AvatarFeminino ||
             Account.Avatar == Protocol.UserInfo.AvatarMasculino))
        {
            origem = Account.Avatar == Protocol.UserInfo.AvatarFeminino ? "sexo feminino" : "sexo masculino";
            return Account.Avatar;
        }

        var doCliente = itens.FirstOrDefault(
            i => i.Equipped && Account!.Avatar != 0 &&
                 (ushort)(i.CatalogId & 0xFFFF) == Account.Avatar);
        if (doCliente is not null) { origem = $"catalogue {doCliente.CatalogId}"; return Account!.Avatar; }

        var equipado = itens.Where(i => i.Equipped && i.CatalogId < CatalogoGearMin)
                            .OrderBy(i => i.CatalogId).FirstOrDefault();
        if (equipado is not null)
        {
            origem = $"catalogue {equipado.CatalogId}";
            return (ushort)(equipado.CatalogId & 0xFFFF);
        }

        origem = "por omissao";
        return Protocol.WaiterInfo.AvatarPorOmissao;
    }

    /// <summary>
    /// O <c>0x0039</c> do tipo 0x02 — a faixa vermelha do topo a dizer que o sistema atribuiu
    /// um item. Procura-se em todas as gravacoes porque a que serve o login nao o tem: quem a
    /// gravou ja' tinha conta feita. Quem o tem e' a conta_nova_s0, que e' suplementar.
    ///
    /// Filtra-se pelo primeiro byte do corpo e nao pelo message id, porque o 0x0039 serve tanto
    /// o aviso como as boas-vindas do canal, e mandar o do canal aqui punha texto a mais no
    /// chat do lobby. Ver Protocol.Aviso.
    /// </summary>
    private ResponseMap.Message? AvisoDoSistema()
    {
        foreach (var fonte in new[] { _map }.Concat(Extras))
            foreach (var m in fonte.TodasDe(Protocol.Aviso.MessageId))
                if (Protocol.Aviso.EDoSistema(m.Body))
                    return m;
        return null;
    }

    private IReadOnlyList<ResponseMap.Message> FindAnywhere(ushort messageId)
    {
        var achado = _map.FindSetContaining(messageId);
        if (achado.Count > 0) return achado;
        foreach (var extra in Extras)
        {
            achado = extra.FindSetContaining(messageId);
            if (achado.Count > 0) return achado;
        }
        return Array.Empty<ResponseMap.Message>();
    }

    /// <summary>
    /// Grupo de arranque de sala, escolhido pelo TIPO de sala e nao pela ordem.
    ///
    /// O grupo certo e' o que traz o <c>CreateRoomAck</c>; a lista de courses (0x0082) so'
    /// aparece nas salas de course, e serve para distinguir os dois. Procurar por ocorrencia
    /// nao funciona: uma gravacao de course tem varios 0x00F0 e o contador acaba num balde
    /// vazio assim que o jogador entra numa segunda sala — foi o que deixou o free mode sem
    /// dados de sala e a bloquear ao entrar.
    /// </summary>
    /// <summary>
    /// Recolhe, de todas as gravacoes, o byte 2 que o servidor real usou para cada musica.
    /// Ver <see cref="ByteDoisPorMusica"/>.
    /// </summary>
    private void RecolherByteDois()
    {
        if (ByteDoisPorMusica.Count > 0) return;
        foreach (var fonte in new[] { _map }.Concat(Extras))
            foreach (var grupo in fonte.AllSetsContaining(GameInfoInf))
                foreach (var m in grupo)
                {
                    if (m.Id != GameInfoInf || m.Body.Length < 8) continue;
                    uint musica = Protocol.GameInfoFraming.ReadSongId(m.Body);
                    ByteDoisPorMusica.TryAdd(musica, m.Body[GameInfoByteDois]);
                }
        Log($"   (GameInfoInf byte 2 known for {ByteDoisPorMusica.Count} song(s))");
    }

    /// <summary>
    /// Ranking do course apontado, para responder ao 0x0083 e ao 0x0085.
    ///
    /// Procura-se pelo COURSE PEDIDO e nao pela ordem em que aconteceu; a chave e' o indice
    /// no cabecalho[3..4]. O cabecalho da resposta leva esse indice, senao o cliente nao
    /// reconhece a resposta a' pergunta que fez e volta a perguntar.
    /// </summary>
    /// <param name="so0x0084">
    /// true para o 0x0083, que so' leva o ranking; false para o 0x0085, que leva tambem o
    /// 0x0086 de confirmacao.
    /// </param>
    /// <summary>Que course um 0x0083/0x0085 nomeia, do cabecalho (em claro). -1 se nao der.</summary>
    private static int CourseDoPedido(byte[] packet) =>
        packet.Length >= 7 ? (int)(BitConverter.ToUInt32(packet, 3) & 0xFFFFu) : -1;

    /// <summary>
    /// UM <c>0x0084</c> GRAVADO COM A TABELA VAZIA, para servir de molde aos courses em que a
    /// conta ainda nao pontuou.
    ///
    /// A course_s1 tem dez: os courses 1, 4, 5, 6, 9, 10, 13, 26, 29 e 32 estavam por jogar no
    /// servidor publico, e a resposta deles e' o corpo todo a zeros com o <c>[5..6]</c> do
    /// cabecalho a zero — que e' exactamente o que este servidor precisa de mandar. E' o mesmo
    /// que o cliente ja' mostra bem no course 34.
    ///
    /// Nao muda nada no fio (o corpo e' zerado e o cabecalho recarimbado de qualquer maneira),
    /// mas parte-se de bytes que se sabe que o cliente aceita como "sem classificacao", em vez
    /// dos de uma tabela cheia de outro course.
    /// </summary>
    private ResponseMap.Message? MoldeDeTabelaVazia()
    {
        if (_moldeProcurado) return _moldeVazio;
        _moldeProcurado = true;

        foreach (var fonte in new[] { _map }.Concat(Extras))
            foreach (var m in fonte.TodasDe(CourseRankAck))
                if (m.Body.Length > 0 && Array.TrueForAll(m.Body, b => b == 0))
                {
                    _moldeVazio = m;
                    Log($"   (empty table template: a 0x0084 of {fonte.Nome})");
                    return _moldeVazio;
                }

        // Sem nenhuma vazia gravada serve qualquer uma — o conteudo e' todo reescrito.
        foreach (var fonte in new[] { _map }.Concat(Extras))
            if (fonte.PrimeiraDe(CourseRankAck) is { } qualquer) { _moldeVazio = qualquer; break; }
        return _moldeVazio;
    }

    /// <summary>
    /// A tabela de high scores de um course, com TODAS AS CONTAS DO FICHEIRO.
    ///
    /// **NAO E' SO' A DE QUEM ESTA' LIGADO.** Era, e por isso um jogador nunca via a pontuacao
    /// de outro: cada conta tinha uma tabela so' sua, e a de quem ainda nao jogasse aquele
    /// course aparecia vazia mesmo que outros ja' la' tivessem estado. Uma tabela de recordes
    /// so' faz sentido a ser comum.
    ///
    /// **O ID DE CADA LUGAR.** O corpo do 0x0084 e' uma cadeia — o campo +41 de cada lugar traz
    /// o id do lugar SEGUINTE (ver Protocol.CourseRank) —, portanto os ids tem de existir e nao
    /// se podem repetir. So' se conhece um id verdadeiro, o de quem esta' ligado (<c>_userId</c>,
    /// que vem do 0x0043 do login); as contas nao tem id proprio nenhum. Aos outros da'-se um
    /// numero estavel bem acima dos reais — os observados nas gravacoes sao 360, 2321, 2322 e
    /// 2395, todos pequenos —, de maneira a nunca colidir com o de quem esta' a ver a tabela.
    /// </summary>
    /// <summary>
    /// O course foi ao fim sem cumprir as condicoes? Devolve true se FALHOU.
    ///
    /// **PORQUE E' QUE ISTO SE DECIDE AQUI E NAO NO ECRA.** O premio de item (<c>0x0089</c>)
    /// sai na rajada de fecho, ANTES do <c>0x0070</c> onde o Total Result e' montado — e um
    /// course falhado nao da' premio. Se se esperasse pelo ecra, o item ja' tinha ido.
    ///
    /// Por isso a soma faz-se aqui com a ultima etapa dobrada por cima do que ja' estava
    /// acumulado, usando as MESMAS contas que o <see cref="Protocol.StageResult.Apply"/> vai
    /// fazer a seguir. Os numeros batem por construcao.
    /// </summary>
    /// <summary>
    /// O MODO RANKING sao TRES etapas somadas, e o total e' que conta para o recorde.
    ///
    /// **O recorde so' se toca na TERCEIRA etapa**, e isso mediu-se: na corrida de 7K o recorde
    /// estava a zero e os totais parciais (194636 e 401299) ja' o batiam, mas o servidor
    /// original manteve o campo a 0 nas duas primeiras e so' escreveu 581128 na terceira. Se
    /// fosse "total corrente > recorde" teria escrito logo na primeira.
    ///
    /// Sao sempre tres — e' a definicao do modo — por isso conta-se e pronto. Uma corrida que
    /// acabe em game over nem chega aqui: o cliente sai da sala sem mandar resultado nenhum.
    /// </summary>
    private const int EtapasDoRanking = 3;

    /// <summary>
    /// O recorde de free mode, calculado no fecho da etapa com as MESMAS contas que o
    /// <see cref="Protocol.StageResult.Apply"/> vai fazer a seguir. Ver
    /// <see cref="DecidirCourse"/>, que faz o mesmo pela mesma razao.
    /// </summary>
    private void ActualizarRecordeDeFreeMode()
    {
        if (Account is null || _lastReport is null) return;

        int notas = BitConverter.ToUInt16(_lastReport, Protocol.StageResult.ReportTotalNotes);
        int combo = (int)BitConverter.ToUInt32(_lastReport, Protocol.StageResult.ReportMaxCombo);
        if (notas <= 0 || _precisao <= 0) return;

        int fim = Protocol.ScoreFormula.Base(notas, _precisao, combo, _breaks)
                + Protocol.ScoreFormula.Disco(_precisao, _breaks).Bonus
                + Protocol.ScoreFormula.BonusDosEffectores(_effectores)
                + Protocol.ScoreFormula.BonusDaVelocidade(_modoVelocidade);

        int antes = Account.RecordeDoCanal(Canal);
        if (fim <= antes) return;

        _novoRecorde = true;
        Account.PorRecordeDoCanal(Canal, fim);
        Store?.Save();
        Log($"   (free mode record on {Canal.ToUpperInvariant()}: {fim} (was {antes}))");
    }

    private void SomarEtapaDeRanking()
    {
        int notas = _lastReport is not null
            ? BitConverter.ToUInt16(_lastReport, Protocol.StageResult.ReportTotalNotes) : 0;
        int combo = _lastReport is not null
            ? (int)BitConverter.ToUInt32(_lastReport, Protocol.StageResult.ReportMaxCombo) : 0;
        if (notas > 0 && _precisao > 0)
            _pontuacaoRanking += Protocol.ScoreFormula.Base(notas, _precisao, combo, _breaks)
                               + Protocol.ScoreFormula.Disco(_precisao, _breaks).Bonus;

        _etapaRanking++;
        Log($"   (ranking {Canal.ToUpperInvariant()}: stage {_etapaRanking} of {EtapasDoRanking}, " +
            $"total {_pontuacaoRanking})");

        if (_etapaRanking < EtapasDoRanking || Account is null) return;

        int antes = Account.RankingDoCanal(Canal);
        if (_pontuacaoRanking > antes)
        {
            Account.PorRankingDoCanal(Canal, _pontuacaoRanking);
            Store?.Save();
            Log($"   (ranking {Canal.ToUpperInvariant()}: new record {_pontuacaoRanking} (was {antes}))");
        }
        else Log($"   (ranking {Canal.ToUpperInvariant()}: {_pontuacaoRanking} does not beat the record {antes})");
    }

    private bool DecidirCourse(CourseTable.Course curso)
    {
        int notas = _lastReport is not null
            ? BitConverter.ToUInt16(_lastReport, Protocol.StageResult.ReportTotalNotes) : 0;
        int combo = _lastReport is not null
            ? (int)BitConverter.ToUInt32(_lastReport, Protocol.StageResult.ReportMaxCombo) : 0;

        int baseEtapa = notas > 0 && _precisao > 0
            ? Protocol.ScoreFormula.Base(notas, _precisao, combo, _breaks) : 0;
        int bonusEtapa = notas > 0 && _precisao > 0
            ? Protocol.ScoreFormula.Disco(_precisao, _breaks).Bonus : 0;

        var precisoes = _precisoesCourse.Concat(new[] { _precisao }).ToList();
        double precisao = precisoes.Count > 0 ? precisoes.Sum() / precisoes.Count : 0;

        var falhas = curso.PorCumprir(precisao,
                                      _pontuacaoCourse + baseEtapa + _bonusCourse + bonusEtapa,
                                      _breaksCourse + _breaks,
                                      combo);

        Log(falhas.Count == 0
            ? $"   (conditions for \"{curso.Nome}\": met — COURSE SUCCESS)"
            : $"   (conditions for \"{curso.Nome}\": FAILED — {string.Join("; ", falhas)} — COURSE FAILED)");
        return falhas.Count > 0;
    }

    private List<Protocol.CourseRank.Entrada> TabelaDoCourse(int course, int lugares) =>
        course < 0 || Store is null
            ? new List<Protocol.CourseRank.Entrada>()
            : Store.TabelaDoCourse(ChaveDoCourse(course), Account?.Name, _userId, lugares);

    /// <summary>
    /// Troca a tabela pelo molde vazio quando NINGUEM tem pontuacao neste course. Sem isto, o
    /// molde era a tabela do course que a gravacao calhasse a ter.
    /// </summary>
    private List<ResponseMap.Message> ComTabelaVazia(List<ResponseMap.Message> respostas, int course)
    {
        if (Store is not null && Store.Accounts.Any(c => c.CourseScores.ContainsKey(ChaveDoCourse(course))))
            return respostas;
        if (MoldeDeTabelaVazia() is not { } vazia) return respostas;
        for (int i = 0; i < respostas.Count; i++)
            if (respostas[i].Id == CourseRankAck) respostas[i] = vazia;
        return respostas;
    }

    private List<ResponseMap.Message> RespostaDeCourse(byte[] packet, bool so0x0084)
    {
        if (packet.Length < 7) return new List<ResponseMap.Message>();

        uint qual = BitConverter.ToUInt32(packet, 3) & 0x00FFFFFFu;   // [6] e' ruido de sessao
        ushort course = (ushort)(qual & 0xFFFF);
        var tentativas = new (ushort Pedido, uint Chave)[]
        {
            (CourseSelectReq, qual), (CourseBrowseReq, qual),
            (CourseSelectReq, course), (CourseBrowseReq, course | 0x200000u),
        };

        var achadas = new List<ResponseMap.Message>();
        foreach (var fonte in new[] { _map }.Concat(Extras))
        {
            foreach (var (pedido, chave) in tentativas)
            {
                var certa = fonte.ForKey(pedido, chave);
                if (certa.Count > 0)
                {
                    achadas = certa.ToList();
                    Log($"   (0x{pedido:x4} course {chave & 0xFFFF} action 0x{(chave >> 16) & 0xFF:x2}: " +
                        $"of {fonte.Nome})");
                    break;
                }
            }
            if (achadas.Count > 0) break;
        }

        // RECURSO: nao ha' gravacao deste course.
        //
        // O 0x0084 que sai daqui EMPRESTA SO' OS BYTES: o corpo e' zerado e reescrito e o
        // cabecalho leva o course e o id certos (ver a reescrita do CourseRankAck). Serve
        // qualquer template, e o resultado e' identico ao dos courses vazios que o servidor
        // real manda — os courses 34 e 40 mostram-no, com tabela limpa e certa no cliente.
        //
        // Quem traz o detalhe do course e' o 0x0086, nao este; por isso deixou de ser aviso.
        if (achadas.Count == 0)
            foreach (var fonte in new[] { _map }.Concat(Extras))
            {
                var alt = fonte.FirstNonEmpty(CourseBrowseReq);
                if (alt.Count > 0)
                {
                    achadas = alt.ToList();
                    Log($"   (course {course} with no recorded 0x0084: table built on " +
                        $"a template from {fonte.Nome})");
                    break;
                }
            }

        if (so0x0084) achadas = achadas.Where(m => m.Id == CourseRankAck).ToList();

        // COURSE SEM PONTUACAO -> TABELA DO MOLDE VAZIO.
        achadas = ComTabelaVazia(achadas, course);

        // A resposta tem de identificar o course perguntado.
        return achadas.Select(m =>
        {
            if (m.Header is null || m.Header.Length < 5) return m;
            var cab = (byte[])m.Header.Clone();
            cab[3] = packet[3];
            cab[4] = packet[4];
            return new ResponseMap.Message(m.Id, cab, m.Body);
        }).ToList();
    }

    /// <summary>
    /// Troca a lista de courses (<c>0x0082</c>, <c>OnCourseListInf</c>) pela lista COMPLETA.
    ///
    /// **E' O SERVIDOR QUE DIZ QUE COURSES EXISTEM.** O corpo desta mensagem e' so' uma
    /// sequencia de indices de 16 bits, sem contador — o numero de courses e' o comprimento
    /// a dividir por dois. A gravacao trazia 24 (48 bytes), que eram os que a conta de quem
    /// gravou tinha desbloqueados, e o cliente mostrava tres paginas de oito. Abrir os
    /// courses no `CourseSection.ini` nao chegava: o .ini diz ao cliente o que cada course E',
    /// esta mensagem diz-lhe QUAIS existem.
    ///
    /// O tamanho e' variavel (confirmado na tabela de tamanhos do cliente: `0x0082 variavel`),
    /// por isso mandar 48 e' so' mandar o dobro dos bytes e acertar o comprimento no
    /// cabecalho, que vive em [3..6].
    ///
    /// **SO' VAO OS QUE SE PODEM MESMO JOGAR.** Quatro courses precisam de uma musica que nao
    /// existe em canal nenhum — o `Crush!`, o `Enjoy! DJMAX!`, o `Fire!` e o `NG`, que pedem as
    /// musicas 177, 189, 176 e 209, todas `offair` no catalogo do cliente. Enquanto os courses
    /// estavam trancados por creditos isto nao se notava; abertos, o jogador escolhia-os, o
    /// course arrancava e ia abaixo ao chegar a' musica que falta.
    ///
    /// **E o `offair` NAO os esconde sozinho** — no servidor original o `Enjoy! DJMAX!` aparece
    /// na lista na mesma, so' que trancado por creditos. Quem decide o que a lista mostra e'
    /// esta mensagem, e mais nada; se o servidor manda o indice, o cliente poe-no la'.
    ///
    /// O filtro e' por CANAL, contra a biblioteca desta sessao: as de 5K e 7K nao tem os mesmos
    /// charts. Ver Net.CourseTable.Jogaveis.
    /// </summary>
    private IReadOnlyList<ResponseMap.Message> ComListaDeCourses(IReadOnlyList<ResponseMap.Message> sala)
    {
        if ((Courses?.Count ?? 0) == 0) return sala;

        var jogaveis = Courses!.Jogaveis(_songs).ToList();
        if (jogaveis.Count == 0) return sala;

        return sala.Select(m =>
        {
            if (m.Id != CourseListInf) return m;

            var corpo = new byte[jogaveis.Count * 2];
            int i = 0;
            foreach (var curso in jogaveis)
            {
                BitConverter.TryWriteBytes(corpo.AsSpan(i, 2), (ushort)curso.Indice);
                i += 2;
            }

            var cab = (byte[])m.Header.Clone();
            int antes = m.Body.Length / 2;
            if (cab.Length >= 7) BitConverter.TryWriteBytes(cab.AsSpan(3, 4), 7 + corpo.Length);
            int fora = Courses.Count - jogaveis.Count;
            Log($"   (course list: {antes} -> {jogaveis.Count}" +
                (fora > 0 ? $"; {fora} hidden for lack of a chart" : "") + ")");
            return new ResponseMap.Message(m.Id, cab, corpo);
        }).ToList();
    }

    /// <summary>As mensagens que compoem a rajada de uma sala, e mais nenhuma.</summary>
    private static readonly ushort[] IdsDaSala =
        { RoomDescInf, CourseListInf, CreateRoomAck, 0x0051, 0x0048 };

    /// <summary>
    /// A rajada gravada da sala do modo pedido.
    ///
    /// **ESCOLHE-SE PELO BYTE DO MODO**, que o servidor real devolve no <c>0x004D</c> (+37) e no
    /// <c>0x0050</c> (+32) — os mesmos 0, 1 e 4 que o cliente pediu no <c>0x004C</c>.
    ///
    /// Antes escolhia-se por "traz a lista de courses ou nao", o que so' distinguia DOIS casos.
    /// Como o ranking nao traz lista nenhuma, era servido com a rajada do free mode — que
    /// anuncia modo 0 — e o cliente, obediente, ia para o free mode.
    ///
    /// **A RAJADA PODE VIR PARTIDA EM DOIS PEDIDOS, e foi isso que fez a primeira correccao
    /// falhar.** Na `end_s1` e na `course_s1` o servidor responde a tudo de uma vez; na
    /// `ranking.txt` responde ao <c>0x004C</c> so' com o <c>0x0050</c> e deixa o
    /// <c>0x004D 0x0051 0x0048</c> para o <c>0x00F0</c> seguinte. Procurar o <c>0x0050</c>
    /// DENTRO do conjunto do <c>0x004D</c> nunca acertava.
    ///
    /// Por isso decide-se pelo <c>0x004D</c>, que esta' sempre no conjunto certo, e o
    /// <c>0x0050</c> vai-se buscar a' mesma gravacao e junta-se a' frente se faltar. Filtra-se
    /// pelos <see cref="IdsDaSala"/> porque o conjunto do <c>0x00F0</c> da `ranking.txt` traz
    /// tambem um <c>0x00FE</c> de inventario, que ali nao tem nada que fazer.
    /// </summary>
    private IReadOnlyList<ResponseMap.Message> GrupoDeSala(byte tipo)
    {
        static bool EDoTipo(ResponseMap.Message m, int offset, byte tipo) =>
            m.Body.Length > offset && m.Body[offset] == tipo;

        foreach (var fonte in new[] { _map }.Concat(Extras))
        {
            foreach (var grupo in fonte.AllSetsContaining(CreateRoomAck))
            {
                if (!grupo.Any(m => m.Id == CreateRoomAck &&
                                    EDoTipo(m, CreateRoomAckTypeOffset, tipo))) continue;

                var rajada = grupo.Where(m => IdsDaSala.Contains(m.Id)).ToList();
                if (!rajada.Any(m => m.Id == RoomDescInf))
                {
                    var desc = fonte.TodasDe(RoomDescInf)
                                    .FirstOrDefault(m => EDoTipo(m, RoomDescTypeOffset, tipo));
                    if (desc.Body is not null) rajada.Insert(0, desc);
                }
                return rajada;
            }
        }

        // Sem rajada daquele modo, vale a antiga regra: a de course traz a lista, a outra nao.
        Log($"   (WARNING: no recorded room burst for mode {tipo}; sending the one for " +
            $"{(tipo == RoomTypeCourse ? "course" : "free mode")})");
        foreach (var fonte in new[] { _map }.Concat(Extras))
            foreach (var grupo in fonte.AllSetsContaining(CreateRoomAck))
                if (grupo.Any(m => m.Id == CourseListInf) == (tipo == RoomTypeCourse))
                    return grupo.ToList();
        return Array.Empty<ResponseMap.Message>();
    }

    private void Log(string s) => _log($"{LogFormat.Sessao(_id)} {s}");

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}















