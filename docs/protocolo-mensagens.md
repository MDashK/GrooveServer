# Catálogo de mensagens — DJMAX Online

Extraído do dispatcher `FUN_004307c0` (switch por msgid) no dump desempacotado do
`DJMax.client`, cruzado com o nome que cada handler regista no seu próprio log.
116 handlers identificados. Os msgid são os bytes 0..1 do pacote, little-endian.

Estes são os handlers do **cliente**, ou seja, mensagens **servidor → cliente**.
As mensagens cliente → servidor partilham frequentemente o mesmo id (ex.: `ConnectReq`
e `OnConnectAck` são ambos `0x000A`).

## Sessão e autenticação

| msgid | dec | Handler |
|---|---|---|
| `0x0003` | 3 | OnPingTestInf |
| `0x0007` | 7 | OnAliveReq |
| `0x0008` | 8 | OnDisconnectPeerInf |
| `0x000A` | 10 | **OnConnectAck** — entrega a chave de sessão no offset 7 |
| `0x000B` | 11 | OnChannelInfoInf |
| `0x000C` | 12 | OnPeerCountInf |
| `0x0010` | 16 | OnAuthenticateInAck |
| `0x0012` | 18 | OnAuthenticateInSNDEKeyReq |
| `0x0014` | 20 | OnAuthenticateInSNDRPWDReq |
| `0x0016` | 22 | OnKeepAuthenticateInAck |
| `0x0018` | 24 | OnLogOutAck |
| `0x001A` | 26 | **OnLogInAck** — status 0x2B = sucesso; rechaveia com 32B no offset 9 |
| `0x010E` | 270 | OnCipherCommandInf |
| `0x012D` | 301 | OnUBSAccountAuthenResAck |
| `0x012E` | 302 | OnUBSAwardInfoInf |
| `0x0130` | 304 | OnUBSAwardAuthenAck |

## Utilizador e perfil

| msgid | dec | Handler |
|---|---|---|
| `0x001E` | 30 | OnUserInfoResNotFound |
| `0x001F` | 31 | OnUserInfoAck |
| `0x0020` | 32 | OnUserIDInfoInf |
| `0x0022` | 34 | OnUserIDInfoAck |
| `0x0024` | 36 | OnUpdateUserIconInf |
| `0x0025` | 37 | OnUpdateUserPropertyInf |
| `0x0026` | 38 | OnUpdateUserPropertyLevelInf |
| `0x0027` | 39 | OnUpdateUserPropertyMoneyInf |
| `0x0028` | 40 | OnUpdateUserPropertyRecordInf |
| `0x0029` | 41 | OnUpdateUserPropertyMISCInf |
| `0x002F` | 47 | OnUpdateUserAccountClassInf |
| `0x0031` | 49 | OnUpdateUserAccountNickAck |
| `0x0033` | 51 | OnUpdateUserProfileAck |
| `0x0043` | 67 | OnUserInfoInf |
| `0x0045` | 69 | OnMessengerInfoInf |

## Inventário e itens

| msgid | dec | Handler |
|---|---|---|
| `0x002A` | 42 | OnUpdateUserInventoryDefaultItemInf |
| `0x002B` | 43 | OnUpdateUserInventoryEventItemInf |
| `0x002C` | 44 | OnUpdateUserInventoryShopItemInf |
| `0x002D` | 45 | OnUpdateUserInventoryMountItemInf |
| `0x002E` | 46 | OnUpdateUserInventoryPresentItemInf |
| `0x0044` | 68 | OnInventoryInfoInf |
| `0x00B3` | 179 | OnCRItemInf |
| `0x00B5`/`0x00B6` | 181/182 | OnGetItemFail / OnGetItemAck |
| `0x00B8`/`0x00B9` | 184/185 | OnItemLevelUpFail / OnItemLevelUpAck |
| `0x00BB`/`0x00BC` | 187/188 | OnUseItemFail / OnUseItemAck |
| `0x00C0` | 192 | OnMissionStandItemInf |
| `0x00C4`/`0x00C6` | 196/198 | OnUseEffectorInf / OnUseEffectorSetInf |
| `0x00C9` | 201 | OnUseMountItemInf |
| `0x00D8` | 216 | OnMountItemAck |
| `0x00DA` | 218 | OnGetPresentItemAck |
| `0x00DC` | 220 | OnDeleteItemAck |
| `0x00DE` | 222 | OnPurchaseItemAck |
| `0x00E0` | 224 | OnResaleItemAck |
| `0x00E4`/`0x00E5` | 228/229 | OnExpiredMountItemInf / OnExpiredShopItemInf |
| `0x00E6` | 230 | OnAlertCreditInf |

## Salas e lobby — o caminho para o free mode

| msgid | dec | Handler |
|---|---|---|
| `0x003A` | 58 | OnRoomInfoUpdateInf |
| `0x003B` | 59 | OnRoomInfoEraseInf |
| `0x003C` | 60 | OnWaiterInfoUpdateInf |
| `0x003D` | 61 | OnWaiterInfoEraseInf |
| `0x0040` | 64 | OnJoinerListStart |
| `0x0041` | 65 | OnJoinerListEnt |
| `0x0042` | 66 | OnJoinerListEnd |
| `0x0047` | 71 | **OnJoinRoomAck** |
| `0x0048` | 72 | OnPostJoinRoomInf |
| `0x004D` | 77 | **OnCreateRoomAck** |
| `0x0050` | 80 | OnRoomDescInf |
| `0x0059` | 89 | OnTeamControlInf |
| `0x005B` | 91 | OnGameTypeInf |
| `0x005E` | 94 | OnReadyInf |
| `0x0074` | 116 | OnLeaveRoomAck |
| `0x009D` | 157 | OnRoomChangeInfoAck |
| `0x00A1`/`0x00A2`/`0x00A7` | 161/162/167 | OnQuickInviteAck / OnInviteReq / OnInviteRejectAck |

## Jogo

| msgid | dec | Handler |
|---|---|---|
| `0x0060` | 96 | **OnStartInf** |
| `0x0061` | 97 | OnJoinEventInf |
| `0x0063` | 99 | OnEventInfoInf |
| `0x0065` | 101 | **OnPlayStartInf** |
| `0x0068` | 104 | OnPlaySkipInf |
| `0x006B` | 107 | OnPlayOverInf |
| `0x006D` | 109 | OnPlayStateInf |
| `0x0070` | 112 | OnStageResultExInf |
| `0x0071` | 113 | OnCheckDataReq |
| `0x0077` | 119 | **OnChangeDiscInf** — troca de música |
| `0x0078` | 120 | OnAwardInfoInf |
| `0x007A` | 122 | OnGameInfoInf |
| `0x007C` | 124 | OnLoadCompleteInf |
| `0x00CA` | 202 | OnStartParameterInf |
| `0x00D3` | 211 | OnGameStartInf |

## Cursos

| msgid | dec | Handler |
|---|---|---|
| `0x0082` | 130 | OnCourseListInf |
| `0x0084` | 132 | OnCourseRankAck |
| `0x0086` | 134 | OnChangeCourseAck |
| `0x0088` | 136 | OnContinueCourseAck |
| `0x0089` | 137 | OnPostCourseItemReq |
| `0x008C` | 140 | OnAwardItemInf |

## Chat, mensagens e sistema

| msgid | dec | Handler |
|---|---|---|
| `0x0037` | 55 | OnWChatInf |
| `0x0039` | 57 | OnChatInf |
| `0x0097` | 151 | OnBigNewsInf |
| `0x00BD`/`0x00BE` | 189/190 | OnGoodluckInf / OnGoodluckListInf |
| `0x00D2` | 210 | OnBillingAuthInf |
| `0x00D4` | 212 | OnUserAlertInf |
| `0x00F1` | 241 | OnMsgNotifyInf |
| `0x00F3` | 243 | OnMsgRegisterUserAck |
| `0x00F4` | 244 | OnMsgRegUserInf |
| `0x00F5` | 245 | OnMsgBlkUserInf |
| `0x00F6` | 246 | OnMsgGroupInf |
| `0x00FC` | 252 | **OnEnvironmentInf** — config; observado com `MIX_FILTER` e `EZ;NM;HD;MX;SC` |
| `0x00FE` | 254 | OnSystemInfoAck |

## Sem nome recuperado

`0x0035` (53), `0x0051` (81), `0x0054` (84), `0x0055` (85), `0x009A` (154), `0x00FB` (251)
— os handlers existem mas não registam um nome no log.
