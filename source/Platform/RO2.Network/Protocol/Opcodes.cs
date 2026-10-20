namespace RO2.Network.Protocol;

/// <summary>
/// Login response codes sent from the server to the client.
/// </summary>
public static class LoginResponseCodes
{
    public const ushort Success = 0;
    public const ushort Code2 = 2;
    public const ushort LocalSystemError = 1001;
    public const ushort AccountNotFound = 1003;
    public const ushort WrongPassword = 1004;
    public const ushort AccountBlocked = 1005;
    public const ushort IpBlocked = 1006;
    public const ushort WrongLoginMethod = 1007;
    public const ushort AlreadyLoggedIn = 1008;
    public const ushort ServiceUnavailable = 1011;
    public const ushort RegionMaintenance = 1020;
    public const ushort UnknownError = 0xFFFF;
}

/// <summary>
/// Zone server C2S RMI ids (client to server), 1301 to 1705.
/// </summary>
/// <remarks>
/// Not every listed id is handled by the server.
/// </remarks>
public static class ZoneC2SOpcodes
{
    // Connection / Authentication
    public const ushort ReqLoginChannel = 1303;         // Zone auth (session GUID + IP)

    // Character Management
    public const ushort Packet_CLMSR_SLOT_LIST = 1414;  // Request character slot list
    public const ushort Packet_CLMSR_SLOT_CREATEPC = 1415; // Create character
    public const ushort Packet_CLMSR_SLOT_DELETEPC = 1416; // Delete character
    public const ushort Packet_CLMSN_LOGIN_PC = 1417;   // Select character / enter world
    public const ushort ReqCheckPCName = 1444;           // Check name availability
    public const ushort ReqReservedPCList = 1590;        // Reserved PC list / ready notification

    // Movement
    public const ushort MovePC = 1305;
    public const ushort ReqPCTeleport = 1371;
    public const ushort NfyPlayerPos = 1465;

    // Combat
    public const ushort ReqAutoAttack = 1359;
    public const ushort ReqNormalAttackStart = 1498;
    public const ushort ReqDamageNormalAttack = 1499;
    public const ushort ReqSkillCastingStart = 1506;
    public const ushort ReqSkillCastingCancel = 1507;
    public const ushort ReqSkillStart = 1508;
    public const ushort ReqSkillDamage = 1509;
    public const ushort ReqSkillDamageTick = 1549;
    public const ushort ReqCastingStart = 1362;
    public const ushort ReqSkill = 1364;
    public const ushort ReqCancelBuff = 1463;

    // Chat
    public const ushort ReqPCChatting = 1315;
    public const ushort ReqPCWhisper = 1316;

    // Items
    public const ushort ReqItemDetailInfo = 1326;
    public const ushort ReqPCItemDrop = 1321;
    public const ushort ReqPCItemPickUp = 1322;
    public const ushort ReqPCItemUse = 1323;
    public const ushort ReqPCItemSwap = 1324;
    public const ushort ReqBuyItem = 1334;
    public const ushort ReqSellItem = 1335;

    // Quick Slots
    public const ushort ReqQuickSlotSwap = 1317;
    public const ushort ReqQuickSlotDel = 1318;
    public const ushort ReqQuickSlotAdd = 1319;

    // Quests
    public const ushort ReqQuestInsert = 1374;
    public const ushort ReqQuestGiveUp = 1375;
    public const ushort ReqQuestExecute = 1376;
    public const ushort ReqQuestComplete = 1377;
    public const ushort ReqQuestPrize = 1378;

    // Party
    public const ushort ReqPartyDiceStart = 1379;
    public const ushort ReqPartyDiceJoin = 1380;
    public const ushort ReqPartyEffectList = 1381;
    public const ushort ReqPartyInvitation = 1382;
    public const ushort ReqPartyJoin = 1383;
    public const ushort ReqPartyOut = 1384;
    public const ushort ReqPartyChangeItemRule = 1385;
    public const ushort ReqPartyChangeExpRule = 1386;
    public const ushort ReqPartyChangeMaster = 1387;
    public const ushort ReqPartyExpulsion = 1388;
    public const ushort ReqPartyNotice = 1389;
    public const ushort ReqPartyRight = 1390;
    public const ushort ReqPartyChangeGroup = 1391;
    public const ushort ReqPartySummary = 1392;

    // Party Matching
    public const ushort ReqPartyMatchingApplyMsg = 1458;
    public const ushort ReqPartyDelMsg = 1459;
    public const ushort ReqPartyMatchingSearchApply = 1460;
    public const ushort ReqPartyMatchingDetail = 1461;
    public const ushort ReqPartyMatchingWhisper = 1462;

    // Party Extended
    public const ushort ReqPartyTypeChange = 1497;
    public const ushort ReqPartyFightingTrim = 1500;
    public const ushort ReqPartyFightingTrimVote = 1501;
    public const ushort ReqPartyJurisdictionIndun = 1502;
    public const ushort ReqPartySubMaster = 1503;
    public const ushort ReqPartyFrameMove = 1504;
    public const ushort ReqPartyDismissSubMaster = 1505;
    public const ushort ReqPartyTacticsTarget = 1529;
    public const ushort ReqPartyDungeonTeleport = 1531;
    public const ushort ReqPartyMemberPCOID = 1539;
    public const ushort ReqPartyDiceChattingCmd = 1545;

    // Party Matching (Find Dungeon)
    public const ushort ReqPartyMatchingInviteMsg = 1554;
    public const ushort ReqPartyMatchingFindDungeon = 1555;
    public const ushort ReqPartyMatchingFindDungeonRandom = 1556;
    public const ushort ReqPartyMatchingInProgressInfo = 1557;
    public const ushort ReqPartyMatchingFindDungeonCancel = 1558;
    public const ushort ReqPartyMatchingFindDungeonJoinSelectBtn = 1559;
    public const ushort ReqPartyMatchingSearchInvite = 1560;
    public const ushort ReqFindParty_Party_SelectRole = 1568;
    public const ushort ReqPartyMatching_Into_Dungeon_Participation = 1569;
    public const ushort ReqPartyMatching_OutDungeon = 1570;
    public const ushort ReqPartyMatching_ReJoinDungeon = 1571;
    public const ushort ReqPartyMemberKickVoteStart = 1572;
    public const ushort ReqPartyKickVote = 1573;
    public const ushort ReqFindParty_Party_SelectRole_Cancel = 1574;

    // Guild
    public const ushort ReqMyGuildInfo = 1424;
    public const ushort ReqGuildCreate = 1426;
    public const ushort ReqGuildDelete = 1427;
    public const ushort ReqGuildInviteMember = 1429;
    public const ushort ReqGuildKickMember = 1431;
    public const ushort ReqGuildUnregisterMe = 1432;

    // NPC / Shop
    public const ushort ReqItemListInNPC = 1338;
    public const ushort ReqCloseItemListInNPC = 1339;

    // Bank
    public const ushort ReqPCBankData = 1340;
    public const ushort ReqBankOpen = 1341;

    // Crafting
    public const ushort ReqCraftListOpen = 1347;
    public const ushort ReqCraftItem = 1349;

    // Trade
    public const ushort ReqPCTradeOpen = 1351;
    public const ushort ReqPCTradeConfirm = 1357;

    // Mail
    public const ushort ReqMailSend = 1393;
    public const ushort ReqMailList = 1394;

    // Auction
    public const ushort ReqAuctionOpen = 1399;

    // Skills
    public const ushort ReqPCSkillLearn = 1366;
    public const ushort ReqSkillAdd = 1365;

    // Channel
    public const ushort ReqChangeCharacterInGame = 1418;
    public const ushort ReqChannelList = 1419;
    public const ushort ReqChannelMove = 1420;

    // Khara (Cards)
    public const ushort ReqKharaAccept = 1433;
    public const ushort ReqCardBookList = 1442;
    public const ushort AnsPing = 1443;

    // Misc
    public const ushort RequestLogout = 1302;
    public const ushort AnsMapLoading = 1304;
    public const ushort ReqActorViewData = 1306;
    public const ushort ReqActorDetailInfo = 1307;
    public const ushort ReqSetBindIndex = 1309;
    public const ushort ReqSocialAnimation = 1369;
    public const ushort ReqPCRevival = 1372;
    public const ushort ReqPCJobChange = 1408;
    public const ushort ReqEndScene = 1496;
    public const ushort ReqReserveDeletePC = 1577;
    public const ushort ReqCancelReservedDeletePC = 1578;

    // Ride glider (short/personal glider; distinct from the far-glider Kafra flight 1515/1528)
    public const ushort ReqTakeGlider = 1313;    // Mount the ride glider
    public const ushort ReqGetOffGlider = 1314;  // Dismount the ride glider at a position

    // Field PvP (open-world PvP / duel; not implemented server-side — handlers parse+log only)
    public const ushort ReqFieldPvP = 1517;
    public const ushort ReqAcceptFiledPvP = 1518;
    public const ushort ReqCancelFiledPvP = 1519;
    public const ushort ReqCountOverFieldPvP = 1520;

    // The rest of the band, in id order. Names follow the client's route table;
    // listing a constant does not imply the server implements it.
    public const ushort SendPacket = 1301;
    public const ushort ActorSitDown = 1308;
    public const ushort ReqPCUseTalentPoint = 1310;
    public const ushort ReqPCInitialTalentPoint = 1311;
    public const ushort ReqChangeCostumeFlag = 1312;
    public const ushort ReqRidingQuickSlot = 1320;
    public const ushort ReqPCItemSwapList = 1325;
    public const ushort ReqRepairItem = 1327;
    public const ushort ReqBreakDownItem = 1328;
    public const ushort ReqEnforceProbability = 1329;
    public const ushort ReqEnforceItem = 1330;
    public const ushort ReqPCItemRune = 1331;
    public const ushort ReqPCStealItemPickUp = 1332;
    public const ushort ReqItemWeaveUse = 1333;
    public const ushort ReqCancelSellItem = 1336;
    public const ushort ReqDestroyItem = 1337;
    public const ushort ReqBankPutItem = 1342;
    public const ushort ReqBankGetItem = 1343;
    public const ushort ReqBankPutGold = 1344;
    public const ushort ReqBankGetGold = 1345;
    public const ushort ReqBankSwapItem = 1346;
    public const ushort ReqCraftListLearn = 1348;
    public const ushort ReqCancleCraftItem = 1350;
    public const ushort ReqPCTradeOpenConfirm = 1352;
    public const ushort ReqPCTradeMove = 1353;
    public const ushort ReqPCTradeDel = 1354;
    public const ushort ReqPCTradeMoney = 1355;
    public const ushort ReqPCTradeLock = 1356;
    public const ushort ReqAutoAttackNotify = 1358;
    public const ushort ReqArcherAttack = 1360;
    public const ushort ReqSkillTeleport = 1361;
    public const ushort ReqCastingCancel07 = 1363;
    public const ushort ReqUseSkillList = 1367;
    public const ushort ReqEffectList = 1368;
    public const ushort ReqEmoticonAnimation = 1370;
    public const ushort ReqPCRevivalHelp = 1373;
    public const ushort ReqMailClose = 1395;
    public const ushort ReqMailGetItem = 1396;
    public const ushort ReqMailRead = 1397;
    public const ushort ReqMailDelete = 1398;
    public const ushort ReqAuctionArticleList = 1400;
    public const ushort ReqAuctionSellList = 1401;
    public const ushort ReqAuctionTenderList = 1402;
    public const ushort ReqAuctionSellStart = 1403;
    public const ushort ReqAuctionSellTenderPrice = 1404;
    public const ushort ReqAuctionSellCancle = 1405;
    public const ushort ReqAuctionBuySellingPrice = 1406;
    public const ushort ReqAuctionTenderJoin = 1407;
    public const ushort ReqPCProJobChange = 1409;
    public const ushort ReqFunctionObjectControl = 1410;
    public const ushort ReqFunctionObjectControlCancel = 1411;
    public const ushort ReqFunctionObjectControlReward = 1412;
    public const ushort ReqTransModeStart = 1413;
    public const ushort ReqChannelMoveLogin = 1421;
    public const ushort Req_CLZSR_INIT_KEYCONFIG = 1422;
    public const ushort Req_CLZSR_UPDATE_KEYCONFIG = 1423;
    public const ushort ReqGuildMemList = 1425;
    public const ushort ReqGuildNoticeUpdate = 1428;
    public const ushort AnsGuildInviteYou = 1430;
    public const ushort ReqSaveKharaTraceList = 1434;
    public const ushort ReqLoadKharaTraceList = 1435;
    public const ushort ReqKharaTimerStart = 1436;
    public const ushort ReqKharaCompleteMission = 1437;
    public const ushort ReqKharaTitleSelect = 1438;
    public const ushort ReqKharaTitleSelectTimeReset = 1439;
    public const ushort ReqSkillChannelingCancel = 1440;
    public const ushort ReqUpdateCardStatus = 1441;
    public const ushort ReqCraftSaleSellerOpenUI = 1445;
    public const ushort ReqCraftSaleRegistItem = 1446;
    public const ushort ReqCraftSaleDeleteItem = 1447;
    public const ushort ReqCraftSaleOpenShop = 1448;
    public const ushort ReqCraftSaleCloseShop = 1449;
    public const ushort ReqCraftSaleChangeShopName = 1450;
    public const ushort ReqCraftSaleBuyerOpenUI = 1451;
    public const ushort ReqCraftSaleShopItemBuy = 1452;
    public const ushort ReqWebMallToken = 1453;
    public const ushort ReqGuildEmblem = 1454;
    public const ushort ReqGuildEmblemUpdate = 1455;
    public const ushort ReqCardComposition = 1456;
    public const ushort ReqMailGetMoney = 1457;
    public const ushort ReqGuildAuthority = 1464;
    public const ushort ReqOpenAggroList = 1466;
    public const ushort ReqGuildDelAuthority = 1467;
    public const ushort ReqGuildModifyAuthority = 1468;
    public const ushort ReqGuildMemeberModifyAuth = 1469;
    public const ushort ReqGuildMasterHandover = 1470;
    public const ushort ReqWebMallItemSearch = 1471;
    public const ushort ReqWebItemAtMyInven = 1472;
    public const ushort ReqOpenPrivateShopSellerUI = 1473;
    public const ushort ReqAddPrivateShopItem = 1474;
    public const ushort ReqDeletePrivateShopItem = 1475;
    public const ushort ReqAddPrivateShopCraft = 1476;
    public const ushort ReqDeletePrivateShopCraft = 1477;
    public const ushort ReqBeginPrivateShop = 1478;
    public const ushort ReqChangePrivateShopName = 1479;
    public const ushort ReqEndPrivateShop = 1480;
    public const ushort ReqOpenPrivateShopBuyerUI = 1481;
    public const ushort ReqBuyPrivateShopItem = 1482;
    public const ushort ReqBuyPrivateShopCraft = 1483;
    public const ushort ReqClosePrivateShopBuyerUI = 1484;
    public const ushort ReqGuildStorageItemPut = 1485;
    public const ushort ReqGuildStorageItemGet = 1486;
    public const ushort ReqGuildFundsInHandDeposit = 1487;
    public const ushort ReqGuildfundsInHandWithdraw = 1488;
    public const ushort ReqOpenGuildStorage = 1489;
    public const ushort ReqCloseGuildStorage = 1490;
    public const ushort ReqGuildStorageStatement_Item = 1491;
    public const ushort ReqGuildStorageStatement_Zeny = 1492;
    public const ushort ReqUseBonusStatPoint = 1493;
    public const ushort ReqChangeProJobID = 1494;
    public const ushort ReqGuildDelPossible = 1495;
    public const ushort ReqRideRidingPet = 1510;
    public const ushort ReqGuildSetBribePercent = 1511;
    public const ushort ReqGuildSkillLearn = 1512;
    public const ushort ReqGuildSkillList = 1513;
    public const ushort ReqRandomItemBatting = 1514;
    public const ushort ReqTakeFarGlider = 1515;
    public const ushort ReqMessengerLoginKey = 1516;
    public const ushort AnsDungeonLevelSelect = 1521;
    public const ushort ReqSkillMoveStart = 1522;
    public const ushort ReqSkillMoveNotify = 1523;
    public const ushort ReqSkillMoveCancel = 1524;
    public const ushort ReqSkillMoveEnd = 1525;
    public const ushort ViewGroupPCMoveTo = 1526;
    public const ushort AnsResultFiledPvP = 1527;
    public const ushort ReqGliderTeleport = 1528;
    public const ushort ReqChallengeNextStage = 1530;
    public const ushort ReqNextStageLevel = 1532;
    public const ushort ReqOpenCloseRangeChatRoom = 1533;
    public const ushort ReqDungeonReset = 1534;
    public const ushort ReqItemTransform = 1535;
    public const ushort ReqInitSimulationStat = 1536;
    public const ushort AnsDungeonInfoConform = 1537;
    public const ushort ResponseHShieldMsg = 1538;
    public const ushort ReqSaveQuestTraceList = 1540;
    public const ushort ReqLoadQuestTraceList = 1541;
    public const ushort ReqOpenItemSocket = 1542;
    public const ushort ReqAttachSocket = 1543;
    public const ushort ReqExtractSocket = 1544;
    public const ushort ReqCardBookPut = 1546;
    public const ushort ReqCardBookTakeOff = 1547;
    public const ushort ReqUseCashItem = 1548;
    public const ushort ReqAccountGiftList = 1550;
    public const ushort ReqGetAccountGift = 1551;
    public const ushort ReqCardBookTakeOff_1Pcs = 1552;
    public const ushort ReqAbilValue = 1553;
    public const ushort ReqSkillStart_Item = 1561;
    public const ushort ReqCanRenameChaNameNow = 1562;
    public const ushort ReqRenameChaName = 1563;
    public const ushort ReqCanRenameChaName = 1564;
    public const ushort ReqFallDamage = 1565;
    public const ushort NfyRandomDungeon_Enter = 1566;
    public const ushort ReqMyEvent = 1567;
    public const ushort ReqUseDyeItem = 1575;
    public const ushort ReqQuestionAnswer_DungeonParticipationJoin = 1576;
    public const ushort ReqCloseWebMall = 1579;
    public const ushort ReqPreViewCacheItem = 1580;
    public const ushort ReqExtendCacheItem = 1581;
    public const ushort ReqJoinUnion = 1582;
    public const ushort ReqWithdrawUnion = 1583;
    public const ushort ReqExtendCacheChangeItem = 1584;
    public const ushort ReqCanRenameGuildName = 1585;
    public const ushort ReqRenameGuildName = 1586;
    public const ushort ReqGuildStorage = 1587;
    public const ushort ReqGiveItemRemainTime = 1588;
    public const ushort ReqReservedPCCount = 1589;
    public const ushort ReqUpdateActivePC = 1591;
    public const ushort ReqGameMallItemListAll = 1592;
    public const ushort ReqGameMallItemList = 1593;
    public const ushort ReqGameMallPurchaseItemList = 1594;
    public const ushort ReqGameMallItemSalesCount = 1595;
    public const ushort ReqGameMallMyInfo = 1596;
    public const ushort ReqGameMallUpdate = 1597;
    public const ushort ReqGameMallPurchase = 1598;
    public const ushort ReqPresentPasswordStatus = 1599;
    public const ushort ReqSetPresentPassword = 1600;
    public const ushort ReqGameMallItemPrice = 1601;
    public const ushort ReqGameMallSearchItem = 1602;
    public const ushort ReqGameMallFindChar = 1603;
    public const ushort ReqWorldList = 1604;
    public const ushort ReqCheckPresentPassword = 1605;
    public const ushort ReqCharNameList = 1606;
    public const ushort ReqColosseumJoin = 1607;
    public const ushort ReqThemeCostumeCompose = 1608;
    public const ushort ReqSkillChannelingComplete = 1609;
    public const ushort ReqCardEnforceProbability = 1610;
    public const ushort ReqCardEnforceItem = 1611;
    public const ushort ReqCutSceneStart = 1612;
    public const ushort ReqCutSceneEnd = 1613;
    public const ushort ReqHelperDeclare = 1614;
    public const ushort ReqMultiTeleportNpc = 1615;
    public const ushort ReqIllusionPyramidGetMyState = 1616;
    public const ushort ReqIllusionPyramidDrawStage = 1617;
    public const ushort ReqIllusionPyramidGetResult = 1618;
    public const ushort ReqIllusionPyramidFinish = 1619;
    public const ushort ReqMCollection = 1620;
    public const ushort ReqMCollectionSelected = 1621;
    public const ushort ReqMCollectionCombinProtein = 1622;
    public const ushort ReqMCollectionUseProtein = 1623;
    public const ushort ReqPCollection = 1624;
    public const ushort ReqPCollectionSelected = 1625;
    public const ushort ReqPCollectionCombinEggBlank = 1626;
    public const ushort ReqPCollectionUsePetEgg = 1627;
    public const ushort ReqPCollectionActivePet = 1628;
    public const ushort ReqPCollectionSetActivePet = 1629;
    public const ushort ReqPCollectionIncubator = 1630;
    public const ushort ReqPCollectionEggIntoIncubator = 1631;
    public const ushort ReqPCollectionCollectEgg = 1632;
    public const ushort ReqGuildSelectRobe = 1633;
    public const ushort ReqGuildListRegisteredRobe = 1634;
    public const ushort ReqGuildTaxInquiry = 1635;
    public const ushort ReqGuildTaxWithdraw = 1636;
    public const ushort ReqSiegeArmsUseStart = 1637;
    public const ushort ReqSiegeArmsUseEnd = 1638;
    public const ushort ReqSiegeArmsRotationStart = 1639;
    public const ushort ReqSiegeArmsRotationStop = 1640;
    public const ushort ReqSiegeArmsUseSkill = 1641;
    public const ushort ReqPlayTimeReward = 1642;
    public const ushort ReqPlayTimeRewardGetItem = 1643;
    public const ushort ReqAttendanceReward = 1644;
    public const ushort ReqAttendanceRewardGetItem = 1645;
    public const ushort ReqRegRevivalMercenary = 1646;
    public const ushort ReqSealItem = 1647;
    public const ushort ReqSealItemClear = 1648;
    public const ushort ReqGetChanceRandomboxPrizeInfo = 1649;
    public const ushort ReqGetSuperChanceRandomboxState = 1650;
    public const ushort ReqGetFeverTimeRandomboxState = 1651;
    public const ushort ReqGuildSkillLearnBook = 1652;
    public const ushort ReqGuildEmergencyCallAnswer = 1653;
    public const ushort ReqRankingList = 1654;
    public const ushort ReqRanking = 1655;
    public const ushort ReqRankerList = 1656;
    public const ushort ReqRankerBuff = 1657;
    public const ushort ReqTRoomJoin = 1658;
    public const ushort ReqTRoomOut = 1659;
    public const ushort ReqTRoomIn = 1660;
    public const ushort ReqTRoomInCancel = 1661;
    public const ushort ReqBestrowAttribute = 1662;
    public const ushort ReqRandomEnforceItem = 1663;
    public const ushort ReqRandomEnforceItemConfirm = 1664;
    public const ushort ReqJobMasteryStart = 1665;
    public const ushort ReqJobMasteryTraining = 1666;
    public const ushort ReqJobMasteryUpgrade = 1667;
    public const ushort ReqRefineItem = 1668;
    public const ushort ReqRefineProbability = 1669;
    public const ushort ReqBeginRefineShop = 1670;
    public const ushort ReqChangeRefineShopName = 1671;
    public const ushort ReqOpenRefineShopBuyerUI = 1672;
    public const ushort ReqRefineShopOpen = 1673;
    public const ushort ReqRandomRefineCommission = 1674;
    public const ushort ReqChangeRandomRefineShopName = 1675;
    public const ushort ReqRandomRefineItem = 1676;
    public const ushort ReqTeleportSavePoint = 1677;
    public const ushort ReqIllusionPyramidItemInfo = 1678;
    public const ushort ReqUpdateDeadPoint = 1679;
    public const ushort ReqDirectReInforce = 1680;
    public const ushort ReqInitGrindingOpt = 1681;
    public const ushort ReqTransferReinforceItem = 1682;
    public const ushort ReqGrindingItemReset = 1683;
    public const ushort AnsRevivalSkill = 1684;
    public const ushort ReqJumpingLevelup = 1685;
    public const ushort ReqGuildHouseLottery = 1686;
    public const ushort ReqGuildHouseTicket = 1687;
    public const ushort ReqGuildHouseResult = 1688;
    public const ushort ReqGuildHouseTeleport = 1689;
    public const ushort ReqGuildHouseLotteryRank = 1690;
    public const ushort ReqRidingExtendItem = 1691;
    public const ushort ReqShowAttendanceState = 1692;
    public const ushort ReqMakeOfferings = 1693;
    public const ushort ReqImprintGuardianSeal = 1694;
    public const ushort ReqConfirmImprintGuardianSealResult = 1695;
    public const ushort ReqReviewPC = 1696;
    public const ushort ReqAllowAnyoneReviewMe = 1697;
    public const ushort NfyMeetDialogNPC = 1698;
    public const ushort ReqDungeonEnteranceInfo = 1699;
    public const ushort ReqOutfitChangeItem = 1700;
    public const ushort ReqOutfitDivideReleaseItem = 1701;
    public const ushort ReqGiveEffectToItem = 1702;
    public const ushort ReqExtendTimeSetItem = 1703;
    public const ushort ReqCheatTool = 1704;
    public const ushort ReqAppearanceChange = 1705;
}

/// <summary>
/// Zone server S2C RMI ids (server to client), 2301 to 3046.
/// </summary>
/// <remarks>
/// Not every listed id is sent by the server.
/// </remarks>
public static class ZoneS2COpcodes
{
    // Connection / Authentication
    public const ushort OnSystemError = 2302;
    public const ushort OnZoneAgentConnectOK = 2303;     // Zone greeting (sent after handshake)
    public const ushort OnCertificationACK = 2304;       // Zone auth acknowledgment
    public const ushort OnCharacterDisconnectNotify = 2305;
    public const ushort OnCharacterLoginFail = 2306;

    // Character Management
    public const ushort OnCharacterListACK = 2307;       // Character list response
    public const ushort OnDeleteCharacterACK = 2308;
    public const ushort OnCreateCharacterACK = 2309;     // Character creation result
    public const ushort OnCharacterDetailACK = 2310;

    // World Entry
    public const ushort OnReceiveMapLoad = 2311;         // Map load / enter world

    // Player Entity
    public const ushort OnChangePC = 2312;
    public const ushort OnMovePC = 2313;
    public const ushort OnRegenPC = 2314;
    public const ushort OnPCViewData = 2315;
    public const ushort OnPCTeleport = 2316;
    public const ushort OnPC_In = 2504;                  // Player spawn
    public const ushort OnPC_Out = 2505;                 // Player despawn

    // NPC Entity
    public const ushort OnRegenNPC = 2317;
    public const ushort OnMoveNPC = 2318;
    public const ushort OnNPCViewData = 2319;
    public const ushort OnNPC_In = 2506;
    public const ushort OnNPC_Out = 2507;
    public const ushort OnDestroyActor = 2320;

    // Game State
    public const ushort OnGameTime = 2321;
    public const ushort OnSitDown = 2322;
    public const ushort OnSitDownNotify = 2323;

    // Chat
    public const ushort OnReceiveChat = 2324;
    public const ushort OnReceiveWhisper = 2325;

    // Bank / Storage
    public const ushort OnBankData = 2338;
    public const ushort OnBankOpen = 2339;
    public const ushort OnBankPutItem = 2340;
    public const ushort OnBankGetItem = 2341;
    public const ushort OnBankPutGold = 2342;
    public const ushort OnBankGetGold = 2343;

    // Items
    public const ushort OnItemInfo = 2326;
    public const ushort OnSwapItem = 2327;
    public const ushort OnPCItemSwapList = 2328;
    public const ushort OnUseItem = 2329;
    public const ushort OnUseItemNotify = 2330;
    public const ushort OnDropItem = 2331;
    public const ushort OnDropItemNotify = 2332;
    public const ushort OnPickupItem = 2333;
    public const ushort OnPickupItemNotify = 2334;
    public const ushort OnCreateItem = 2335;
    public const ushort OnRepairItem = 2336;
    public const ushort OnRuneEnchant = 2337;
    public const ushort OnPartyNotifyRight = 2369;
    public const ushort OnDestroyItem = 2379;
    public const ushort OnBuyItem = 2375;
    public const ushort OnSellItem = 2376;
    public const ushort OnUpdateInvenSize = 2378;

    // Quick Slots
    public const ushort OnQuickSlotSaveData = 2472;
    public const ushort OnAddQuickSlot = 2473;
    public const ushort OnDelQuickSlot = 2474;
    public const ushort OnSwapQuickSlot = 2475;

    // Combat
    public const ushort OnAttackFrom = 2390;
    public const ushort OnDeadMe = 2391;
    public const ushort OnDeadNotify = 2392;
    public const ushort OnLevelUp = 2394;
    public const ushort OnLevelUpNotify = 2395;
    public const ushort OnChangeState = 2398;
    // Combat / Stats
    public const ushort OnActorAutoHeal = 2401;
    public const ushort OnRevivalHelp = 2402;
    public const ushort OnNpcEscape = 2403;
    // HP/MP update aliases (use OnAttackFrom/OnDeadMe for direct updates)
    public const ushort OnUpdateHP = OnAttackFrom;   // 2390
    public const ushort OnUpdateMP = OnDeadMe;       // 2391

    // Emotes / Social
    public const ushort OnSocialAni = 2396;
    public const ushort OnEmoticonAni = 2397;
    public const ushort OnRevival = 2399;

    // Save Point
    public const ushort OnSavePoint = 2440;
    public const ushort NfyAttackDamage = 2696;

    // Skills
    public const ushort OnLearnSkill = 2407;
    public const ushort OnSkillCasting = 2410;
    public const ushort OnSkillFire = 2412;
    public const ushort AnsSkillCastingStart = 2708;
    public const ushort NfySkillCastingStart = 2709;
    public const ushort AnsUseSkillStart = 2715;
    public const ushort NfyUseSkillStart = 2716;
    public const ushort NfySkillDamage = 2718;

    // Stats / Buffs
    public const ushort OnFillupEXP = 2371;
    public const ushort OnZenyChange = 2435;
    public const ushort OnAddEffect = 2492;
    public const ushort OnDelEffect = 2494;
    public const ushort OnPcTrait = 2498;

    // Quests
    public const ushort OnQuestInsert = 2421;
    public const ushort OnQuestGiveup = 2420;
    public const ushort OnQuestCompleteNotify = 2427;
    public const ushort OnQuestPrize = 2429;
    public const ushort OnQuestError = 2431;

    // Party
    public const ushort OnPartyInvite = 2346;
    public const ushort OnPartyJoin = 2347;
    public const ushort OnPartyError = 2348;
    public const ushort OnPartyOut = 2349;
    public const ushort OnPartyChangeItem = 2350;
    public const ushort OnPartyChangeExp = 2351;
    public const ushort OnPartyChangeMaster = 2352;
    public const ushort OnPartyExpulsion = 2353;

    // Remaining Zone S2C ids, in id order
    public const ushort OnBankItemSwap = 2344;
    public const ushort OnBankBagSizeChange = 2345;
    public const ushort OnPartyRight = 2354;
    public const ushort OnPartyChangeGroup = 2355;
    public const ushort OnPartySummary = 2356;
    public const ushort OnPartyNotifyCreate = 2357;
    /// <summary>
    /// Party destroy notification (RMI 2358). Body is 14 bytes: [01 0c][u16 12][u16 msgTag 58162][u32 0][u32 party handle]. Built by <c>PartyStatePackets.BuildOnPartyNotifyDestroy2358</c>.
    /// </summary>
    public const ushort OnPartyNotifyDestroy = 2358;
    public const ushort OnPartyNotifyJoin = 2359;
    /// <summary>
    /// Party member-out notification (RMI 2360). Body is 23 bytes: [01 15][u16 21][u16 msgTag 58164][u32 0][u32 party handle][u64 member GID][u8 kind]. Built by <c>PartyNotifyOutPackets.BuildPartyNotifyOut2360</c>.
    /// </summary>
    public const ushort OnPartyNotifyOut = 2360;
    public const ushort OnPartyNotifyUpdate = 2361;
    public const ushort OnPartyNotifyGetItem = 2362;
    public const ushort OnPartyNotifyChangeItem = 2363;
    public const ushort OnPartyNotifyChangeExp = 2364;
    public const ushort OnPartyNotifyChangeMaster = 2365;
    public const ushort OnPartyNotifyExpulsion = 2366;
    public const ushort OnPartyNotifyLayoff = 2367;
    public const ushort OnPartyNotifyMessage = 2368;
    public const ushort OnPartyNotifyChangeGroup = 2370;
    public const ushort OnPcRoomUserNotify = 2372;
    public const ushort OnHackShieldDisconnect = 2373;
    public const ushort OnHeartBeatItem = 2374;
    public const ushort OnCancelSellItem = 2377;
    public const ushort OnChangeEquipItemHideFlag = 2380;
    public const ushort OnOpenTradePC = 2381;
    public const ushort OnBeginTradePC = 2382;
    public const ushort OnBeginTradePCNotify = 2383;
    public const ushort OnDragTradePC = 2384;
    public const ushort OnDelTradePC = 2385;
    public const ushort OnDragGold = 2386;
    public const ushort OnLockTradePC = 2387;
    public const ushort OnConfirmTradePC = 2388;
    public const ushort OnConfirmTradePCNotify = 2389;
    public const ushort OnCompensation = 2393;
    public const ushort OnNpcChangeTarget = 2400;
    public const ushort OnNpcHelp = 2404;
    public const ushort OnNpcChangeTeam = 2405;
    public const ushort OnPKPoint = 2406;
    public const ushort OnSkillTeleport = 2408;
    public const ushort OnSkillTeleportEndNotify = 2409;
    public const ushort OnSkillCastingNotify = 2411;
    public const ushort OnSkillFireNotify = 2413;
    public const ushort OnResetSkill = 2414;
    public const ushort OnCancelSkill = 2415;
    public const ushort OnCancelSkillNotify = 2416;
    public const ushort OnBreakDownItem = 2417;
    public const ushort OnEnforceProability = 2418;
    public const ushort OnEnforceItem = 2419;
    public const ushort OnQuestInsertNotify = 2422;
    public const ushort OnQuestTakeResponse = 2423;
    public const ushort OnQuestAddItem = 2424;
    public const ushort OnQuestMonsterOrGO = 2425;
    public const ushort OnQuestUseItem = 2426;
    public const ushort OnQuestInCompleteNotify = 2428;
    public const ushort OnQuestPrizeNotify = 2430;
    public const ushort OnQuestUpdateNotify = 2432;
    public const ushort OnQuestTimerStart = 2433;
    public const ushort OnQuestFailed = 2434;
    public const ushort OnWorldWeather = 2436;
    public const ushort OnNpcTalk = 2437;
    public const ushort OnCashItemTeleport = 2439;
    public const ushort OnMountGlider = 2441;
    public const ushort OnUnmountGlider = 2442;
    public const ushort OnNPCSubStateChange = 2443;
    public const ushort OnMailList = 2444;
    public const ushort OnMailSend = 2445;
    public const ushort OnMailRead = 2446;
    public const ushort OnMailGetItem = 2447;
    public const ushort OnMailDelete = 2448;
    public const ushort OnMailNew = 2449;
    public const ushort OnSummonCreate = 2450;
    public const ushort OnSummonDestroy = 2451;
    public const ushort OnSummonChangeAI = 2452;
    public const ushort OnSummonChangeSkillMode = 2453;
    public const ushort OnAuctionOpen = 2454;
    public const ushort OnAuctionSellList = 2455;
    public const ushort OnAuctionSearch = 2456;
    public const ushort OnAuctionTenderList = 2457;
    public const ushort OnAuctionSellStart = 2458;
    public const ushort OnAuctionSellTenderPrice = 2459;
    public const ushort OnAuctionSellCancel = 2460;
    public const ushort OnAuctionBuySellingPrice = 2461;
    public const ushort OnAuctionTenderJoin = 2462;
    public const ushort OnAuctionPageUpdate = 2463;
    public const ushort OnMountGliderNotify = 2464;
    public const ushort OnUnmountGliderNotify = 2465;
    public const ushort OnPlayMovie = 2466;
    public const ushort OnSlotChangePCName = 2467;
    public const ushort OnLootingBoxRouletteOpen = 2468;
    public const ushort OnLootingBoxRouletteResult = 2469;
    public const ushort OnLootingBoxRouletteRefuse = 2470;
    public const ushort OnLootingAuthority = 2471;
    public const ushort OnIDCardPassword = 2476;
    public const ushort OnIDCardPasswordResult = 2477;
    public const ushort OnGameClientMakeResponse = 2478;
    public const ushort OnPremiumData = 2479;
    public const ushort OnSkillTranslate = 2480;
    public const ushort OnSkillTranslateNotify = 2481;
    public const ushort OnStealItemList = 2482;
    public const ushort OnStealItemPickUp = 2483;
    public const ushort OnStealItemPickUpNotify = 2484;
    public const ushort OnStealItemError = 2485;
    public const ushort OnDestroyItemNotify = 2486;
    public const ushort OnUseSkillList = 2488;
    public const ushort OnSkillGet = 2489;
    public const ushort OnAttackFromFail = 2490;
    public const ushort OnGetEffectList = 2491;
    public const ushort OnDelEffectSuccess = 2493;
    public const ushort OnDotDamageNotify = 2495;
    public const ushort OnChangeAbnormalSP = 2496;
    public const ushort OnChangeActorMoveSpeed = 2497;
    public const ushort OnPcTraitChange = 2499;
    public const ushort OnPcTraitChangeNotify = 2500;
    public const ushort OnPcSkillChangeCastingTime = 2501;
    public const ushort OnPcKharaPointChangeNotify = 2502;
    public const ushort OnUpdateItemDurability = 2503;
    public const ushort OnAutoAttack_List = 2508;
    public const ushort OnAutoAttack_Result = 2509;
    public const ushort OnAutoAttack_List_Queue = 2510;
    public const ushort OnItemWeavingUse = 2511;
    public const ushort OnFuncObject_In = 2512;
    public const ushort OnFuncObject_Out = 2513;
    public const ushort OnFuncObject_Regen = 2514;
    public const ushort OnFuncObjectControl = 2515;
    public const ushort OnFuncObjectControlNotify = 2516;
    public const ushort OnFuncObject_Cancel = 2517;
    public const ushort OnFuncObject_CancelNotify = 2518;
    public const ushort OnFuncObjectRewardResult = 2519;
    public const ushort OnFuncObjectChangeState = 2520;
    public const ushort OnFuncObjectError = 2521;
    public const ushort OnAllVehicleList = 2522;
    public const ushort OnVehicleQuickSlot = 2523;
    public const ushort OnChangeVehicleQuickSlot = 2524;
    public const ushort OnArcherAttack = 2525;
    public const ushort OnNotifyProJobExpChange = 2526;
    public const ushort OnNotifyProJobLevelUpPC = 2527;
    public const ushort OnNotifyProJobChange = 2528;
    public const ushort OnNotifyTransChargeGague = 2529;
    public const ushort OnAckTransModeStart = 2530;
    public const ushort OnNotifyTransModeStart = 2531;
    public const ushort OnNotifyTransModeEnd = 2532;
    public const ushort OnNotifyTransModeGauge = 2533;
    public const ushort OnNotifyCanGatherDeadNpc = 2534;
    public const ushort OnNotifyCraftIndex = 2535;
    public const ushort OnNotifyCoolTimeWaitCraftIndex = 2536;
    public const ushort OnAckCraftOpen = 2537;
    public const ushort OnAckCraftLearn = 2538;
    public const ushort OnAckMakeCraftItem = 2539;
    public const ushort OnNotifyMakeCraftItem = 2540;
    public const ushort OnAckMakeCraftItemInfo = 2541;
    public const ushort OnGetTalentPoint = 2542;
    public const ushort OnUseTalentPoint = 2543;
    public const ushort OnInitTalentPoint = 2544;
    public const ushort OnEventBroadCastMsgNotify = 2545;
    public const ushort Packet_CLMSA_INDUN_RESET_ACK = 2546;
    public const ushort Packet_CLMSA_BIND_INDUN_INFO_ACK = 2547;
    public const ushort Packet_MSCLN_BIND_INDUN_NOTICE = 2548;
    public const ushort Packet_ZSCLN_SYSTEM_ERROR = 2549;
    public const ushort Packet_LACLN_LOGIN_CONNECTED = 2550;
    public const ushort Packet_ZACLN_LOGIN_CONNECTED = 2551;
    public const ushort Packet_LACLN_LOGIN_OVERLAPPING = 2552;
    public const ushort Packet_LSCLN_LOGIN_OVERLAPPING_PROCESSING = 2553;
    public const ushort Packet_CLZAA_LOGIN_ACCOUNT = 2554;
    public const ushort AckChannelListInGame = 2555;
    public const ushort AnsChannelMove = 2556;
    public const ushort AnsChannelMoveLogin = 2557;
    public const ushort Notify_ZSCLN_DB_ACCOUNT_CONFIG = 2558;
    public const ushort AnsMyGuildInfo = 2559;
    public const ushort AnsGuildMemList = 2560;
    public const ushort AnsGuildCreate = 2561;
    public const ushort AnsGuildDelete = 2562;
    public const ushort AnsGuildUpdateNotice = 2563;
    public const ushort NfyGuildNoticeUpdated = 2564;
    public const ushort AnsGuildInviteMember = 2565;
    public const ushort NfyGuildInviteYouReq = 2566;
    public const ushort NfyGuildInviteMemberAns = 2567;
    public const ushort AnsGuildInviteYouAns = 2568;
    public const ushort AnsGuildKickMember = 2569;
    public const ushort NfyGuildKickMember = 2570;
    public const ushort NfyGuildMemberRemove = 2571;
    public const ushort AnsGuildUnregisterMe = 2572;
    public const ushort NfyGuildMemberAdd = 2573;
    public const ushort AnsKharaAccept = 2574;
    public const ushort NfyKharaComplete = 2575;
    public const ushort NfyKharaReward = 2576;
    public const ushort NfyKharaInComplete = 2577;
    public const ushort NfyKharaError = 2578;
    public const ushort NfyKharaUpdateCount = 2579;
    public const ushort AnsKharaTimerStart = 2580;
    public const ushort NfyKharaFailed = 2581;
    public const ushort AnsKharaTitleSelect = 2582;
    public const ushort AnsKharaTitleSelectTimeRest = 2583;
    public const ushort OnSkillChannelingCancelACK = 2584;
    public const ushort OnSkillChannelingCancelNotify = 2585;
    public const ushort OnSkillChannelingCompleteNotify = 2586;
    public const ushort AnsCardBookList = 2587;
    public const ushort AnsRegistCardBook = 2588;
    public const ushort AnsUpdateCardStatus = 2589;
    public const ushort AnsGuildKickMemberNfy = 2590;
    public const ushort NfyPCViewInfoUpdate = 2591;
    public const ushort NfyKharaTitleChange = 2592;
    public const ushort ReqPing = 2593;
    public const ushort AnsAdminCommand = 2594;
    public const ushort OnPcInitTrait = 2595;
    public const ushort NotifyChangeP2PGroupID = 2598;
    public const ushort OnAckCraftSaleSellerOpenUI = 2599;
    public const ushort OnAckCraftSaleRegistItem = 2600;
    public const ushort OnAckCraftSaleDeleteItem = 2601;
    public const ushort OnAckCraftSaleOpenShop = 2602;
    public const ushort OnNfyCraftSaleOpenShop = 2603;
    public const ushort OnAckCraftSaleCloseShop = 2604;
    public const ushort OnNfyCraftSaleCloseShop = 2605;
    public const ushort OnAckCraftSaleChangeShopName = 2606;
    public const ushort OnNfyCraftSaleChangeShopName = 2607;
    public const ushort OnAckCraftSaleBuyerOpenUI = 2608;
    public const ushort OnAckCraftSaleShopItemBuy = 2609;
    public const ushort OnNfyCraftSaleShopItemBuy = 2610;
    public const ushort AnsWebMallToken = 2611;
    public const ushort AnsGuildEmblem = 2612;
    public const ushort AnsUpdateGuildEmblem = 2613;
    public const ushort NfyUpdateGuildEmblem = 2614;
    public const ushort NfySwimmingGuage = 2615;
    public const ushort AckCardComposition = 2616;
    public const ushort OnMailGetMoney = 2617;
    public const ushort AnsPartyMatchingMsg = 2618;
    public const ushort AnsPartyApplyMsg = 2619;
    public const ushort AnsPartyDelMsg = 2620;
    public const ushort AnsPartyMemberSearch = 2621;
    public const ushort AnsPartyApplySearch = 2622;
    public const ushort AnsPartyMatchingDetail = 2623;
    public const ushort AnsPartyMatchingError = 2624;
    public const ushort AnsPartyMatchingWhisper = 2625;
    public const ushort NfyCharTransform = 2626;
    public const ushort NfyPCItemTrait = 2627;
    public const ushort AnsGuildAuthority = 2628;
    public const ushort AnsAggroList = 2629;
    public const ushort NfyAggroChanged = 2630;
    public const ushort NfyTargetInfo = 2631;
    public const ushort AnsGuildDelAuthority = 2632;
    public const ushort AnsGuildModifyAuthority = 2633;
    public const ushort AnsGuildMemberModifyAuthority = 2634;
    public const ushort AnsGuildMasterHandover = 2635;
    public const ushort NfyGuildDelAuthority = 2636;
    public const ushort NfyGuildModifyAuthority = 2637;
    public const ushort NfyWebMallItems = 2638;
    public const ushort AnsWebItemSearch = 2639;
    public const ushort AnsWebGiftItemSearch = 2640;
    public const ushort AnsWebItemAtMyInven = 2641;
    public const ushort AnsWebItemError = 2642;
    public const ushort AnsActorIconInfo = 2643;
    public const ushort NfyGuildMemberModifyAuthority = 2644;
    public const ushort NfyGuildMasterHandover = 2645;
    public const ushort AnsOpenPrivateShopSellerUI = 2646;
    public const ushort AnsAddPrivateShopItem = 2647;
    public const ushort AnsDeletePrivateShopItem = 2648;
    public const ushort AnsAddPrivateShopCraft = 2649;
    public const ushort AnsDeletePrivateShopCraft = 2650;
    public const ushort AnsBeginPrivateShop = 2651;
    public const ushort AnsChangePrivateShopName = 2652;
    public const ushort AnsEndPrivateShop = 2653;
    public const ushort NfyCraftSaleShopItemBuy = 2654;
    public const ushort NfyPrivateShopItemSaled = 2655;
    public const ushort NfyPrivateShopSaledList = 2656;
    public const ushort AnsOpenPrivateShopBuyerUI_ItemList = 2657;
    public const ushort AnsOpenPrivateShopBuyerUI_CraftList = 2658;
    public const ushort AnsBuyPrivateShopItem = 2659;
    public const ushort AnsBuyPrivateShopCraft = 2660;
    public const ushort AnsClosePrivateShopBuyerUI = 2661;
    public const ushort NfyAnsBuyPrivateShopItem = 2662;
    public const ushort NfyBeginPrivateShop = 2663;
    public const ushort NfyEndPrivateShop = 2664;
    public const ushort NfyChangePrivateShopName = 2665;
    public const ushort NfyTerminatePrivateShop = 2666;
    public const ushort NfyTerminatePrivateShopForBuyer = 2667;
    public const ushort NfyServerTimeToLoginPC = 2669;
    public const ushort AnsGuildStorageItemPut = 2670;
    public const ushort AnsGuildStorageItemGet = 2671;
    public const ushort AnsGuildFundsInHandDeposit = 2672;
    public const ushort AnsGuildFundsInHandWithdraw = 2673;
    public const ushort AnsOpenGuildStorage = 2674;
    public const ushort NfyAddGuildStorage_Item = 2675;
    public const ushort NfyDelGuildStorage_Item = 2676;
    public const ushort NfyUpdateGuildFundsInHand = 2677;
    public const ushort AnsGuildStorageStatement_Item = 2678;
    public const ushort AnsGuildStorageStatement_Zeny = 2679;
    public const ushort OnAnsUseBonusStatPoint = 2680;
    public const ushort OnAnsSimulationStatPoint = 2681;
    public const ushort NfyChangeProJob = 2682;
    public const ushort AnsChangeProJobInfo = 2683;
    public const ushort AnsEndScene = 2684;
    public const ushort NfyChangeProJobDelQuickSlot = 2685;
    public const ushort NfyChageProJobGuardianSkill = 2686;
    public const ushort NfyChangeProJobQuest = 2687;
    public const ushort AnsGuildDelPossible = 2688;
    public const ushort NfyExcuteBuffHandling = 2689;
    public const ushort NfyPlayScene = 2690;
    public const ushort AnsPartyTypeChange = 2691;
    public const ushort NfyPartyTypeChange = 2692;
    public const ushort NfyAuctionSystemMessage = 2693;
    public const ushort NfyNormalAttackStart = 2694;
    public const ushort AnsErrorNormalAttack = 2695;
    public const ushort NfyPartyFightingTrimResult = 2697;
    public const ushort AnsPartyFightingTrim = 2698;
    public const ushort NfyPartyFightingTrimStart = 2699;
    public const ushort AnsPartyFightingTrimVote = 2700;
    public const ushort AnsPartyJurisdictionIndun = 2701;
    public const ushort AnsPartySubMaster = 2702;
    public const ushort NfyPartySubMaster = 2703;
    public const ushort AnsPartyFrameMove = 2704;
    public const ushort NfyPartyFrameMove = 2705;
    public const ushort AnsPartyDismissSubMaster = 2706;
    public const ushort NfyPartyDismissSubmaster = 2707;
    public const ushort AnsSkillCastingCancel = 2710;
    public const ushort NfySkillCastingCancel = 2711;
    public const ushort NfyDropRidingPet = 2712;
    public const ushort AnsRandomItemInBoxList = 2713;
    public const ushort AnsRandomItemSelected = 2714;
    public const ushort AnsSkillError = 2717;
    public const ushort AnsRideRidingPet = 2719;
    public const ushort NfyRideRidingPet = 2720;
    public const ushort AnsGuildSetBribePercent = 2721;
    public const ushort AnsGuildSkillLearn = 2722;
    public const ushort AnsGuildSkillList = 2723;
    public const ushort NfyGuildSkillLearn = 2724;
    public const ushort NfyGuildBribeHunting = 2725;
    public const ushort NfyGuildBribePCLevelUp = 2726;
    public const ushort AnsGuildGMInitSkillList = 2727;
    public const ushort NfyGuildLevelUP = 2728;
    public const ushort NfyNpcChat = 2729;
    public const ushort NfyGuildHandoverMemberBribePoint_ToGuildMaster = 2730;
    public const ushort OnMountFarGlider = 2731;
    public const ushort OnUnmountFarGlider = 2732;
    /// <summary>
    /// Zone-to-chat handoff token. Sent with <c>AnsMapLoading</c> (1304); the client replays its body as the chat login request.
    /// </summary>
    /// <remarks>
    /// Body: [data_len:u32=56][char_uid:u64][token:16][ip_len:u16 (wchar count)][ip: UTF-16LE][port:u16].
    /// </remarks>
    public const ushort AnsPlayerLoginKey = 2733;
    public const ushort AnsGliderRideScene = 2734;
    public const ushort AnsCertifyFieldPvP = 2735;
    public const ushort NfyResultFiledPvP = 2736;
    public const ushort NfyAcceptFiledPvP = 2737;
    public const ushort NfyCancelFiledPvP = 2738;
    public const ushort NfyGetOutFiledPvP = 2739;
    public const ushort NfyGetInFiledPvP = 2740;
    public const ushort ReqDungeonLevelSelect = 2741;
    public const ushort AnsReqErrorFiledPvP = 2742;
    public const ushort OnSendDependentDungeonInfo = 2743;
    public const ushort AnsSkillMoveStart = 2744;
    public const ushort NfySkillMoveNotify = 2745;
    public const ushort AnsSkillMoveCancel = 2746;
    public const ushort NfySkillMoveCancel = 2747;
    public const ushort AnsSkillMoveEnd = 2748;
    public const ushort NfySkillMoveEnd = 2749;
    public const ushort ViewGroupPCMoveTo = 2750;
    public const ushort AnsPartyTacticsTarget = 2751;
    public const ushort AnsPartyTacticsTargetError = 2752;
    public const ushort NfyReadyOkNextLevelOnStage = 2753;
    public const ushort NfyFailureLevelOnStage = 2754;
    public const ushort NfyBeginLevelOnStage = 2755;
    public const ushort NfyEndOfStageDungeon = 2756;
    public const ushort NfyCardDurabilityUpdate = 2757;
    public const ushort NfyStartFiledPvP = 2758;
    public const ushort OnSendDependentDungeonMission = 2759;
    public const ushort NfyDungeonLevelSelectInfo = 2760;
    public const ushort NfyDungeonBindResetWarning = 2761;
    public const ushort AnsOpenCloseRangeChatRoom = 2762;
    public const ushort NfyOpenCloseRangeChatRoom = 2763;
    public const ushort NfyJoinCloseRangeChatRoom = 2764;
    public const ushort NfyExitCloseRangeChatRoom = 2765;
    public const ushort NfyNewMasterCloseRangeChatRoom = 2766;
    public const ushort AnsPCMoveStateChange = 2767;
    public const ushort NfyPCMoveStateChange = 2768;
    public const ushort NfySkillMoveStart = 2769;
    public const ushort NfyUpdateJoinMemberCountCloseRangeChatRoom = 2770;
    public const ushort AnsDungeonReset = 2771;
    public const ushort AnsItemTransform = 2772;
    public const ushort AnsInitSimulationStat = 2773;
    public const ushort NfyDungeonBindReset = 2774;
    public const ushort NfyDungeonBindMissionInfo = 2775;
    public const ushort RequestHShieldMsg = 2776;
    public const ushort NfyHShieldErrorDetected = 2777;
    public const ushort AnsPartyMemberPCOID = 2778;
    public const ushort AnsLoadQuestTraceList = 2779;
    public const ushort AnsLoadKharaTraceList = 2780;
    public const ushort NfyCardEquipTakeOff = 2781;
    public const ushort NfyTimedItemInfo = 2782;
    public const ushort NfyExpiredItemInfo = 2783;
    public const ushort NtyUpdateQuestReAct = 2784;
    public const ushort AnsOpenItemSocket = 2785;
    public const ushort AnsAttachSocket = 2786;
    public const ushort AnsExtractSocket = 2787;
    public const ushort NfyPartyDiceChattingCmd = 2788;
    public const ushort AnsUseCashItem = 2789;
    /// <summary>
    /// Corpse item-list close notification (RMI 2790). This server never sends it.
    /// </summary>
    /// <remarks>
    /// Body is 12 bytes: [u32 corpse handle, low byte 0x13][u64 0]. Loot distribution on this server does not use corpse item lists, so the notification is not needed.
    /// </remarks>
    public const ushort NfyPartyItemListClose = 2790;
    public const ushort AnsCardBookPut = 2791;
    public const ushort AnsCardBookTakeOff = 2792;
    public const ushort NfyCardBookChargedTime = 2793;
    public const ushort NfyCardBookChargedTimeEnd = 2794;
    public const ushort NfyItemUpdate = 2795;
    public const ushort NfyJoinDungeonIsRandom = 2796;
    public const ushort NfySkillDamageTick = 2797;
    public const ushort NfyAccountGiftReceived = 2798;
    public const ushort NfyAccountGiftInfo = 2799;
    public const ushort AnsGetAccountGift = 2800;
    public const ushort AnsCardBookTakeOff_1Pcs = 2801;
    public const ushort NfyEventEnter = 2802;
    public const ushort AnsAbilValue = 2803;
    public const ushort AnsPartyMatchingFindDungeon = 2804;
    public const ushort AnsPartyMatchingFindDungeonRandom = 2805;
    public const ushort AnsPartyMatchingInProgressInfo = 2806;
    public const ushort AnsPartyMatchingFindDungeonCancel = 2807;
    public const ushort NfyPartyMatchingFindDungeonStartJoinMessage = 2808;
    public const ushort AnsPartyMatchingFindDungeonJoinSelectBtn = 2809;
    public const ushort NfyPartyMatchingFindDungeonJoin_Fail = 2810;
    public const ushort NfyServerTime = 2811;
    public const ushort AnsCanRenameChaNameNow = 2812;
    public const ushort AnsRenameChaName = 2813;
    public const ushort AnsCanRenameChaName = 2814;
    public const ushort NfyFallDamage = 2815;
    public const ushort NfyRandomDungeonCompleteGift = 2816;
    public const ushort NfyCoolTimeClear = 2817;
    public const ushort AnsMyEvent = 2818;
    public const ushort NfyNpcEffect = 2819;
    public const ushort OnPreOccupyCharacterNameACK = 2820;
    public const ushort NfyAccountGiftCount = 2821;
    public const ushort NfyFindParty_Party_Dungeon_SelectRolePlease = 2822;
    public const ushort NfyDungeonFind_PC_Private_Indun_Join = 2823;
    public const ushort NfyDungeonFind_InProgress_JoinMsg = 2824;
    public const ushort NfyDungeonFind_Party_Cancel = 2825;
    public const ushort AnsPartyMemberKickVoteStart = 2826;
    public const ushort AnsPartyKickVote = 2827;
    public const ushort NfyResultPartyKickVote = 2828;
    public const ushort NfyPartyMemberKickVote = 2829;
    public const ushort AnsFindParty_Party_SelectRole_Cancel = 2830;
    public const ushort NfyResult_FindParty_Party_SelectRole = 2831;
    public const ushort AnsPartyMatching_OutDungeon = 2832;
    public const ushort AnsPartyMatching_ReJoinDungeon = 2833;
    public const ushort AnsFindParty_Party_SelectRole = 2834;
    public const ushort AnsUseDyeItem = 2835;
    public const ushort NfySpinelRevivalInfo = 2836;
    public const ushort NfyPCCafeRevivalInfo = 2837;
    public const ushort OnVehicleListUpdate = 2838;
    public const ushort NfyTPlusInfo = 2839;
    public const ushort ReqQuestionDungeonParticipationJoin = 2840;
    public const ushort NfyCardBookAlramEndTime = 2841;
    public const ushort AnsReserveDeletePC = 2842;
    public const ushort AnsCancelReservedDeletePC = 2843;
    public const ushort NfyGraInternetCafeGradeChanged = 2844;
    public const ushort NfyEventClear = 2845;
    public const ushort AnsPreViewCacheItem = 2846;
    public const ushort AnsExtendCacheItem = 2847;
    public const ushort NfyPCEquipView = 2848;
    public const ushort AnsJoinUnion = 2849;
    public const ushort AnsWithdrawUnion = 2850;
    public const ushort AnsExtendCacheChangeItem = 2851;
    public const ushort AnsPCPageCount = 2852;
    public const ushort NfyFuncObjectControlStatus = 2853;
    public const ushort AnsCanRenameGuildName = 2854;
    public const ushort AnsRenameGuildName = 2855;
    public const ushort AnsGuildStorage = 2856;
    public const ushort AnsGiveItemRemainTime = 2857;
    public const ushort NfyDeleteAcceptedKhara = 2858;
    public const ushort NfyUpdateKharaCompleted = 2859;
    public const ushort NfyUpdateQuestCompleted = 2860;
    public const ushort AnsReservedPCCount = 2861;
    public const ushort AnsReservedPCList = 2862;
    public const ushort NfyOpenRemoteShop = 2863;
    public const ushort NfyOpenRemoteBank = 2864;
    public const ushort AnsGameMallItemList = 2865;
    public const ushort AnsGameMallItemListAll = 2866;
    public const ushort AnsGameMallItemSalesCount = 2867;
    public const ushort AnsGameMallMyInfo = 2868;
    public const ushort AnsGameMallUpdate = 2869;
    public const ushort AnsGameMallPurchase = 2870;
    public const ushort AnsPresentPasswordStatus = 2871;
    public const ushort AnsSetPresentPassword = 2872;
    public const ushort AnsGameMallItemPrice = 2873;
    public const ushort AnsGameMallPurchaseItemList = 2874;
    public const ushort AnsGameMallSearchItem = 2875;
    public const ushort AnsGameMallFindChar = 2876;
    public const ushort NfyPvpPCInfo = 2877;
    public const ushort NtfPvpResult = 2878;
    public const ushort NfyColosseumStart = 2879;
    public const ushort NfyColosseumBeforehandStart = 2880;
    public const ushort NfyColosseumWin = 2881;
    public const ushort NfyColosseumJoinCount = 2882;
    public const ushort NfyColosseumRoundStateChange = 2883;
    public const ushort NfyColosseumRoundClear = 2884;
    public const ushort NfyColosseumLimitJoin = 2885;
    public const ushort NfyColosseumRoundStartPlayerList = 2886;
    public const ushort NfyColosseumRankingUpdate = 2887;
    public const ushort NfyColosseumPCKillCount = 2888;
    public const ushort NfyColosseumScoreBoard = 2889;
    public const ushort AnsWorldList = 2890;
    public const ushort AnsCheckPresentPassword = 2891;
    public const ushort AnsCharNameList = 2892;
    public const ushort AnsGmCommand = 2893;
    public const ushort NfyCountLimitItemNotice = 2894;
    public const ushort NfyWebEventWinnerNotice = 2895;
    public const ushort AnsThemeCostumeCompose = 2896;
    public const ushort OnSkillChannelingCompleteACK = 2897;
    public const ushort OnCardEnforceProability = 2898;
    public const ushort OnCardEnforceItem = 2899;
    public const ushort AnsCutSceneStart = 2900;
    public const ushort AnsCutSceneEnd = 2901;
    public const ushort AnsHelperDeclare = 2902;
    public const ushort NfyHelperNotice = 2903;
    public const ushort NfyCutSceneStart = 2904;
    public const ushort AnsMultiTeleportNpc = 2905;
    public const ushort AnsIllusionPyramidGetMyState = 2906;
    public const ushort AnsIllusionPyramidDrawStage = 2907;
    public const ushort AnsIllusionPyramidGetResult = 2908;
    public const ushort AnsIllusionPyramidFinish = 2909;
    public const ushort NfySiegeUnitPosition = 2910;
    public const ushort AnsMCollection = 2911;
    public const ushort AnsMCollectionSelectedSkill = 2912;
    public const ushort NfyMCollection = 2913;
    public const ushort NfyMCollectionSkill = 2914;
    public const ushort NfyMCollectionSelectedSkill = 2915;
    public const ushort AnsPCollection = 2916;
    public const ushort AnsPCollectionSelectedSkill = 2917;
    public const ushort AnsPCollectionSetActivePet = 2918;
    public const ushort NfyPCollection = 2919;
    public const ushort NfyPCollectionSkill = 2920;
    public const ushort NfyPCollectionSelectedSkill = 2921;
    public const ushort AnsMCollectionCombinProtein = 2922;
    public const ushort AnsMCollectionUseProtein = 2923;
    public const ushort AnsPCollectionCombinEggBlank = 2924;
    public const ushort AnsPCollectionUsePetEgg = 2925;
    public const ushort AnsPCollectionIncubator = 2926;
    public const ushort NfyGuildRobeChanged = 2927;
    public const ushort AnsGuildSelectRobe = 2928;
    public const ushort AnsGuildListRegisteredRobe = 2929;
    public const ushort NfySiegeStartBeforehandStart = 2930;
    public const ushort NfySiegeStart = 2931;
    public const ushort NfySiegeOccupy = 2932;
    public const ushort NfySiegeStatusInfo = 2933;
    public const ushort NfySiegeHPStatus = 2934;
    public const ushort AnsGuildTaxInquiry = 2935;
    public const ushort AnsGuildTaxWithdraw = 2936;
    public const ushort AnsSiegeArmsUseStart = 2937;
    public const ushort AnsSiegeArmsUseEnd = 2938;
    public const ushort NfySiegeArmsSettingChanged = 2939;
    public const ushort NfySiegeArmsUserChanged = 2940;
    public const ushort NfySiegeArmsRotationStart = 2941;
    public const ushort NfySiegeArmsRotationStop = 2942;
    public const ushort AnsSiegeArmsUseSkill = 2943;
    public const ushort AnsPlayTimeReward = 2944;
    public const ushort AnsPlayTimeRewardGetItem = 2945;
    public const ushort AnsAttendanceReward = 2946;
    public const ushort AnsAttendanceRewardGetItem = 2947;
    public const ushort NfyNpcBattleLevelUp = 2948;
    public const ushort AnsRegRevivalMercenary = 2949;
    public const ushort NfyRevivalMercenaryState = 2950;
    public const ushort NfyEventSpawnNDropNotice = 2951;
    public const ushort AnsUpdatePCPos = 2952;
    public const ushort NfyMarketRiseUpEnd = 2953;
    public const ushort AnsSealItem = 2954;
    public const ushort AnsSealItemClear = 2955;
    public const ushort AnsGetChanceRandomboxPrizeInfo = 2956;
    public const ushort AnsGetSuperChanceRandomboxState = 2957;
    public const ushort AnsGetFeverTimeRandomboxState = 2958;
    public const ushort AnsGuildSkillLearnBook = 2959;
    public const ushort AnsGuildEmergencyCallAsk = 2960;
    public const ushort NfySiegeStartBeforehandRestart = 2961;
    public const ushort NfySkillStartError = 2962;
    public const ushort NfySkillDamageError = 2963;
    public const ushort AnsRankingList = 2964;
    public const ushort AnsRanking = 2965;
    public const ushort AnsRankerList = 2966;
    public const ushort AnsRankerBuff = 2967;
    public const ushort NfyRankerChange = 2968;
    public const ushort NfyEntryPartyError = 2969;
    public const ushort NfyTRoomJoinMsg = 2970;
    public const ushort NfyColosseumJoinMsg = 2971;
    public const ushort NfyTRoomPartyState = 2972;
    public const ushort NfyColosseumPartyState = 2973;
    public const ushort AnsTRoomInCancel = 2974;
    public const ushort AnsColosseumInCancel = 2975;
    public const ushort NfyTRoomState = 2976;
    public const ushort NfyTRoomBattleState = 2977;
    public const ushort NfyTRoomBattleStateAll = 2978;
    public const ushort NfyTRoomPcKill = 2979;
    public const ushort NfyTRoomNpcKill = 2980;
    public const ushort NfyTRoomEnd = 2981;
    public const ushort NfyRankingTopList = 2982;
    public const ushort AnsBestrowAttribute = 2983;
    public const ushort AnsRandomEnforceItem = 2984;
    public const ushort AnsRandomEnforceItemConfirm = 2985;
    public const ushort NfyJobMasteryPointChange = 2986;
    public const ushort AnsJobMasteryStarter = 2987;
    public const ushort AnsJobMasteryTraining = 2988;
    public const ushort AnsJobMasteryUpgrade = 2989;
    public const ushort NfyTRoomNpcKillByNpc = 2990;
    public const ushort NfyTRoomRematchingParty = 2991;
    public const ushort NfyColosseumRematchingParty = 2992;
    public const ushort NfyTRoomEffectList = 2993;
    public const ushort AnsTRoomIn = 2994;
    public const ushort NfyTRoomPcKillByNpc = 2995;
    public const ushort OnRefineProability = 2996;
    public const ushort OnRefineItem = 2997;
    public const ushort NfyRefineItemNotice = 2998;
    public const ushort AnsBeginRefineShop = 2999;
    public const ushort NfyRefineCommissionToBuyer = 3000;
    public const ushort OnChangeRefineShopName = 3001;
    public const ushort NfyChangeRefineShopName = 3002;
    public const ushort NfyBeginRefineShop = 3003;
    public const ushort NfySiegeGuildTime = 3004;
    public const ushort OnRefineItemToSeller = 3005;
    public const ushort NfyRandomRefineCommissionToBuyer = 3006;
    public const ushort AnsChangeRandomRefineShopName = 3007;
    public const ushort NfyChangeRandomRefineShopName = 3008;
    public const ushort OnRandomRefineItem = 3009;
    public const ushort AnsTeleportSavePoint = 3010;
    public const ushort AnsIllusionPyramidItemInfo = 3011;
    public const ushort AnsDirectReInforce = 3012;
    public const ushort AnsInitGrindingOpt = 3013;
    public const ushort AnsTransferReinforceItem = 3014;
    public const ushort AnsGrindingItemReset = 3015;
    public const ushort NfyRevivalSkill = 3016;
    public const ushort AnsJumpingLevelup = 3017;
    public const ushort AnsGuildHouseLottery = 3018;
    public const ushort AnsGuildHouseTicket = 3019;
    public const ushort AnsGuildHouseResult = 3020;
    public const ushort AnsGuildHouseLotteryRank = 3021;
    public const ushort AnsGuildHouseTeleport = 3022;
    public const ushort AnsRidingExtendItem = 3023;
    public const ushort NfyGuildHouseAnnounce = 3024;
    public const ushort NfySkillCoolTime = 3025;
    public const ushort AnsShowAttendanceState = 3026;
    public const ushort NfyShowAttendanceState = 3027;
    public const ushort NfyCoolTimeLockReleased = 3028;
    public const ushort AnsMakeOfferings = 3029;
    public const ushort NfyWorldEventTrigger = 3030;
    public const ushort AnsImprintGuardianSeal = 3031;
    public const ushort AnsConfirmImprintGuardianSealResult = 3032;
    public const ushort AnsReviewPC = 3033;
    public const ushort NfyPushingStart = 3034;
    public const ushort AnsDungeonEnteranceInfo = 3035;
    public const ushort AnsAllowAnyoneReviewMe = 3036;
    public const ushort AnsOutfitChangeItem = 3037;
    public const ushort AnsOutfitDivideReleaseItem = 3038;
    public const ushort AnsGiveEffectToItem = 3039;
    public const ushort NfyCurrencyChanged = 3040;
    public const ushort NfyTimeSetItemStart = 3041;
    public const ushort NfyTimeSetItemEnd = 3042;
    public const ushort AnsExtendTimeSetItem = 3043;
    public const ushort NfyYouAreStressed = 3044;
    public const ushort AnsCheatTool = 3045;
    public const ushort AnsAppearanceChange = 3046;

    // Name Check
    public const ushort OnCheckPCName = 2596;            // Name check result

    // Misc Notifications
    public const ushort OnCommonError = 2438;
    public const ushort OnReceiveNotice = 2668;
    public const ushort OnCoolTimeDataNotify = 2597;
    public const ushort OnUpdatePCSaveData = 2487;

    // --- Aliases for handler code readability ---
    public const ushort ZoneGreeting = OnZoneAgentConnectOK;      // 2303
    public const ushort ZoneAuthAck = OnCertificationACK;          // 2304
    public const ushort ZoneCharacterList = OnCharacterListACK;    // 2307
    public const ushort CharCreationAck = OnCreateCharacterACK;    // 2309
    public const ushort NameCheckResult = OnCheckPCName;           // 2596

    // The rest of the band, in id order. Names follow the client's route table;
    // listing a constant does not imply the server implements it.
    public const ushort SendPacket = 2301;
}

/// <summary>
/// Login server C2S RMI ids (client to server).
/// </summary>
public static class LoginC2SOpcodes
{
    public const ushort ReqVersionCheck = 12001;          // VersionCheck
    public const ushort ReqLogin = 12002;                 // Login
    public const ushort ReqGraLogin = 12003;              // ExtendedAuth (Gravity login)
    public const ushort ReqChannelList = 12004;           // RequestServerList
    public const ushort ReqLoginChannel = 12005;          // SelectServer
    public const ushort ReqLogOut = 12006;
    public const ushort SendPacket = 12007;               // Generic packet forward
    public const ushort ReqServerStatus = 12008;
    public const ushort ReqSecondPassword = 12009;        // Request PIN dialog
    public const ushort ReqInputSecondPassword = 12010;   // Submit PIN digits
    public const ushort ReqModifySecondPassword = 12011;  // Change PIN
    public const ushort ReqSteamLogin = 12012;            // Regional: Steam
    public const ushort ReqAeriaGamesLogin = 12013;       // Regional: Aeria Games
    public const ushort ReqLoginOTP = 12014;              // Regional: OTP
    public const ushort ReqGFLogin = 12015;               // Regional: GameForge
    public const ushort ReqLytoLogin = 12016;             // Regional: Lyto
    public const ushort ReqDSLogin = 12017;               // Regional: DS

    // --- Aliases for backward compatibility with handler code ---
    public const ushort VersionCheck = ReqVersionCheck;           // 12001
    public const ushort Login = ReqLogin;                         // 12002
    public const ushort ExtendedAuth = ReqGraLogin;               // 12003
    public const ushort RequestServerList = ReqChannelList;       // 12004
    public const ushort SelectServer = ReqLoginChannel;           // 12005
    public const ushort ClientStateRequest = ReqInputSecondPassword; // 12010
    public const ushort SecondPassword = ReqModifySecondPassword; // 12011
}

/// <summary>
/// Login server S2C RMI ids (server to client).
/// </summary>
public static class LoginS2COpcodes
{
    public const ushort AckVersionCheck = 12501;           // VersionCheckResult
    public const ushort AckLogin = 12502;                  // LoginResult
    public const ushort AckChannelList = 12503;            // ServerGroupList
    public const ushort AckLoginChannel = 12504;           // SelectServerAck
    public const ushort SendPacket = 12505;                // Generic (carries the character list)
    public const ushort AckServerStatus = 12506;
    public const ushort AnsSecondPassword = 12507;         // PostLoginState / PIN response
    public const ushort AnsInputSecondPassword = 12508;    // ConnectionNotify

    // --- Aliases for backward compatibility with handler code ---
    public const ushort VersionCheckResult = AckVersionCheck;    // 12501
    public const ushort LoginResult = AckLogin;                  // 12502
    public const ushort ServerGroupList = AckChannelList;        // 12503
    public const ushort SelectServerAck = AckLoginChannel;       // 12504
    public const ushort CharacterList = SendPacket;              // 12505
    public const ushort CharacterCreateResult = AckServerStatus; // 12506
    public const ushort PostLoginState = AnsSecondPassword;      // 12507
    public const ushort ConnectionNotify = AnsInputSecondPassword; // 12508
}

/// <summary>
/// Chat server C2S RMI ids (client to server).
/// </summary>
/// <remarks>
/// Constant names follow the client's route table, including its spelling.
/// </remarks>
public static class ChatC2SOpcodes
{
    /// <summary>
    /// Chat login request. The body is the token from <c>AnsPlayerLoginKey</c> (2733). Answered with <c>AnsLoginMessenger</c> (23501).
    /// </summary>
    public const ushort ReqLoginMessenger = 23001;
    /// <summary>
    /// Add-friend request. The body holds the target name as a VarBlob32: [CompactLen nChars][UTF-16LE]. Answered with <c>AnsAddFriend</c> (23508).
    /// </summary>
    public const ushort ReqAddFriend = 23002;
    /// <summary>Friend-list request. Answered with <c>AnsFriendList</c> (23505).</summary>
    public const ushort ReqFriendList = 23003;
    /// <summary>Group-list request. Answered with <c>AnsGroupList</c> (23507).</summary>
    public const ushort ReqGroupList = 23004;
    /// <summary>Deletes a friend.</summary>
    public const ushort ReqDelFriend = 23005;
    /// <summary>Adds a user to the block list.</summary>
    public const ushort ReqAddBlockList = 23006;
    /// <summary>Removes a user from the block list.</summary>
    public const ushort ReqDelBlockList = 23007;
    /// <summary>Block-list request. Answered with <c>AnsBlockList</c> (23506).</summary>
    public const ushort ReqBlockList = 23008;
    /// <summary>Public channel list request. Answered with <c>AnsPublicChatRoomList</c> (23513).</summary>
    public const ushort ReqPublicChatRoomList = 23009;
    /// <summary>Private room list request. Answered with <c>AnsPrivateChatRoomList</c> (23514).</summary>
    public const ushort ReqPrivateChatRoomList = 23010;
    /// <summary>Joins a private chat room.</summary>
    public const ushort ReqPrivateChatRoomJoin = 23011;
    /// <summary>Joins a public chat room.</summary>
    public const ushort ReqPublicChatRoomJoin = 23012;
    /// <summary>Leaves a chat room.</summary>
    public const ushort ReqChatRoomExit = 23013;
    /// <summary>Changes a private room's password.</summary>
    public const ushort ReqPrivateChatRoomChangePass = 23014;
    /// <summary>Kicks a user from a private room.</summary>
    public const ushort ReqPrivateChatRoomInUserKick = 23015;
    /// <summary>Invites a user to a private room.</summary>
    public const ushort ReqPrivateChatRoomInvate = 23016; // client's spelling
    /// <summary>Requests the member list of a private room.</summary>
    public const ushort ReqPrivateRoomInMember = 23017;
    /// <summary>
    /// Room or channel chat message. Body: [innerLen:u32][channelGuid:16][channelIdx:u16][senderHandle:u64][msgLen:u8][msg: UTF-16LE]. Broadcast as <c>NfyRoomChat_Public</c> (23528).
    /// </summary>
    public const ushort ReqRoomChat = 23018;
    /// <summary>Sets or requests a public channel color.</summary>
    public const ushort ReqPublicChatRoomColor = 23019;
    /// <summary>Creates or joins a close-range (proximity) chat room.</summary>
    public const ushort ReqJoinCloseRangeChatRoom = 23020;
    /// <summary>Kicks a user from a close-range chat room.</summary>
    public const ushort ReqKickCloseRangeChatRoom = 23021;
    /// <summary>
    /// Leaves a close-range chat room. The body is the 16-byte room guid. Answered with <c>AnsExitCloseRangeChatRoom</c> (23541).
    /// </summary>
    public const ushort ReqExitCloseRangeChatRoom = 23022;
    /// <summary>Sends a close-range chat message.</summary>
    public const ushort ReqChatCloseRangeChatRoom = 23023;
}

/// <summary>
/// Chat server S2C RMI ids (server to client).
/// </summary>
/// <remarks>
/// Constant names follow the client's route table, including its spelling.
/// </remarks>
public static class ChatS2COpcodes
{
    /// <summary>
    /// Chat login acknowledgment, sent after <c>ReqLoginMessenger</c>. The body is a u16 result code, 0 for success.
    /// </summary>
    public const ushort AnsLoginMessenger = 23501;
    /// <summary>
    /// Notifies that a registered friend came online. Body: VarBlob32 of [uid:u64][CompactLen nChars][name: UTF-16LE].
    /// </summary>
    public const ushort NfyLoginPlayer_RegisterFriend = 23502;
    /// <summary>Notifies that a registered friend went offline. The body is the 8-byte uid.</summary>
    public const ushort NfyLogoutPlayer_RegisterFriend = 23503;
    /// <summary>Notifies that a user went offline in a private room. Body: [uid:u64][roomGuid:16].</summary>
    public const ushort NfyLogoutPlayer_PrivateChatRoom = 23504;
    /// <summary>
    /// Friend-list reply to <c>ReqFriendList</c>. Body: [reserved:u16=0][innerLen:u32][count:u16]{[uid:u64][nameLen:u8][name: UTF-16LE][tail:8]}.
    /// </summary>
    public const ushort AnsFriendList = 23505;
    /// <summary>
    /// Block-list reply to <c>ReqBlockList</c>. Same envelope as <c>AnsFriendList</c>, with a one-byte trailer per record.
    /// </summary>
    public const ushort AnsBlockList = 23506;
    /// <summary>Group-list reply to <c>ReqGroupList</c>. The body is the 8-byte empty list when there are no groups.</summary>
    public const ushort AnsGroupList = 23507;
    /// <summary>Reply to <c>ReqAddFriend</c>. The body carries the new friend's uid, name and channel.</summary>
    public const ushort AnsAddFriend = 23508;
    public const ushort AnsDelFriend = 23509;
    public const ushort AnsAddBlock = 23510;
    public const ushort AnsDelBlock = 23511;
    /// <summary>
    /// Add-friend notification. The body is a WStr: [widthTag:u8=02][CompactLen nChars][UTF-16LE].
    /// </summary>
    public const ushort NfyAddFriend = 23512;
    /// <summary>
    /// Public channel list reply to <c>ReqPublicChatRoomList</c>. Populates the client's channel tabs. Body: [reserved:u16=0][innerLen:u32][count:u16]{[guid:16][color:u32][index:u8][type:u8][nameLen:u8][name: UTF-16LE]}.
    /// </summary>
    public const ushort AnsPublicChatRoomList = 23513;
    /// <summary>Private room list reply to <c>ReqPrivateChatRoomList</c>. The body is the 8-byte empty list when there are no rooms.</summary>
    public const ushort AnsPrivateChatRoomList = 23514;
    public const ushort AnsPrivateChatRoomJoin = 23515;
    public const ushort NfyPrivateChatRoomJoinUser = 23516;
    public const ushort AnsPrivateRoomInMember = 23517;
    public const ushort AnsPublicChatRoomJoin = 23518;
    public const ushort AnsChatRoomExit = 23519;
    public const ushort NfyChatRoomExitMmeberInfo = 23520; // client's spelling
    public const ushort AnsPrivateChatRoomChangePass = 23521;
    public const ushort AnsPrivateChatRoomInUserKick = 23522;
    public const ushort NfyPrivateChatRoomKickUser = 23523;
    public const ushort NfyPrivateChatRoomMessage_ToKickUser = 23524;
    public const ushort AnsPrivateChatRoomInvite = 23525;
    public const ushort NfyPrivateChatRoomInvate_InRoomMember = 23526; // client's spelling
    public const ushort NfyPrivateChatRoomInvate_JoinMember = 23527; // client's spelling
    /// <summary>
    /// Broadcast of a channel chat message to the channel members. Body: [innerLen:u32][channelGuid:16][channelIdx:u16][nameLen:u8][name: UTF-16LE][msgLen:u8][msg: UTF-16LE].
    /// </summary>
    public const ushort NfyRoomChat_Public = 23528;
    /// <summary>Broadcast of a private room chat message.</summary>
    public const ushort NfyRoomChat_Private = 23529;
    public const ushort AnsPublicChatRoomColor = 23530;
    public const ushort NfyFriendChannelChange = 23531;
    public const ushort NfyInviteRoomInfo = 23532;
    public const ushort NfyPrivateChatRoom_ChangeRoomMaster = 23533;
    /// <summary>
    /// Per-channel auto-join notification, sent after the login acknowledgment. Body: [channelIndex:u16][channelGuid:16].
    /// </summary>
    public const ushort NfyPublicChatRoomAutoJoin = 23534;
    /// <summary>Per-channel auto-exit notification. Body: [channelIndex:u16][channelGuid:16].</summary>
    public const ushort NfyPublicChatRoomAutoExit = 23535;
    public const ushort NfyChannelChatError = 23536;
    public const ushort NfyChannelDisconnect = 23537;
    public const ushort AnsJoinCloseRangeChatRoom = 23538;
    public const ushort AnsKickCloseRangeChatRoom = 23539;
    public const ushort NfyKickCloseRangeChatRoom_ToKickUser = 23540;
    /// <summary>Reply to <c>ReqExitCloseRangeChatRoom</c>. Body: [result:u16=0][roomGuid:16].</summary>
    public const ushort AnsExitCloseRangeChatRoom = 23541;
    public const ushort AnsChatCloseRangeChatRoom = 23542;
    public const ushort NfyCreateCloseRangeChatRoom = 23543;
    public const ushort NfyJoinCloseRangeChatRoom = 23544;
}

/// <summary>
/// ProudNet internal C2S RMI ids (client to server), handled by the internal RMI handler.
/// </summary>
public static class ProudNetC2SOpcodes
{
    public const ushort ClientReady = 1001;
    public const ushort NotifyCSSessionKey = 1002;
    public const ushort NotifyServerConnectionHint = 1003;
    public const ushort NotifyCSP2PSessionKey = 1004;
    public const ushort P2PGroup_MemberJoinAck = 1005;
    public const ushort P2PGroup_MemberJoined = 1006;
    public const ushort NotifyConnectPeer = 1007;
    public const ushort NotifyConnectPeerFailed = 1008;
    public const ushort NotifyP2PLinkEstablished = 1009;
    public const ushort NotifyP2PDisconnected = 1010;
    public const ushort ReliablePing = 1011;
    public const ushort ShutdownTcp = 1012;
    public const ushort ShutdownTcpHandshake = 1013;
    public const ushort NotifyLog = 1014;
    public const ushort NotifyLogHolepunchFreqFail = 1015;
    public const ushort NotifyNatDevice = 1016;
    public const ushort NotifyJitDirectP2PTriggered = 1017;
    public const ushort NotifySuperPeerAuth = 1018;
    public const ushort NotifyConnectionPeerReady = 1019;
}

/// <summary>
/// ProudNet internal S2C RMI ids (server to client), sent through <c>SendInternalRmiAsync</c>.
/// </summary>
public static class ProudNetS2COpcodes
{
    public const ushort JoinServerComplete = 1108;
}

/// <summary>
/// Result codes for the server selection acknowledgment.
/// </summary>
public static class SelectServerResultCodes
{
    public const byte Success = 0;
    public const byte CannotFindAccount = 1;
    public const byte ChannelNotExist = 2;
    public const byte ChannelFull = 3;
    public const byte ServerSystemError = 4;
    public const byte DuplicatedUser = 5;
    public const byte ChannelFull2 = 6;
    public const byte ServerInspected = 7;
}
