using System;
using System.IO;
using System.Windows.Forms;
using System.Linq;
using Advanced_Combat_Tracker;
using System.Threading;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Windows.Forms.Integration;
using Lotlab.PluginCommon.FFXIV.Parser.Packets;
using Lotlab.PluginCommon.FFXIV.Parser;
using Lotlab.PluginCommon.FFXIV;
using Lotlab.PluginCommon.Updater;
using Lotlab.PluginCommon;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

public static class Extensions
{
    public static T[] SubArray<T>(this T[] array, int offset, int length)
    {
        T[] result = new T[length];
        Array.Copy(array, offset, result, 0, length);
        return result;
    }
}

namespace HousingCheck
{
    public class HousingCheck : IActPluginV1
    {
        /// <summary>
        /// 插件对象
        /// </summary>
        ACTPluginProxy ffxivPlugin;

        List<(DateTime, string)> LogQueue = new List<(DateTime, string)>();

        /// <summary>
        /// 是否加载完成
        /// </summary>
        bool initialized = false;

        /// <summary>
        /// 有手动上报任务
        /// </summary>
        bool ManualUpload = false;

        DataStorage storage;

        /// <summary>
        /// 无操作自动保存的时间
        /// </summary>
        TimeSpan AutoSaveAfter = TimeSpan.FromSeconds(20);

        Notifier notifier;

        /// <summary>
        /// 自动保存worker
        /// </summary>
        private BackgroundWorker AutoSaveThread;

        /// <summary>
        /// Log队列
        /// </summary>
        private BackgroundWorker LogQueueWorker;

        /// <summary>
        /// 状态信息
        /// </summary>
        Label statusLabel;

        Config config = new Config();
        SimpleLoggerSync logger;
        NetworkParser parser = new NetworkParser();
        UploadApi api;
        ExtendedUpdater updater;
        OpcodeRepo opcodeRepo;

        void IActPluginV1.DeInitPlugin()
        {
            if (initialized)
            {
                ffxivPlugin.DataSubscription.NetworkReceived -= NetworkReceived;
                ffxivPlugin.DataSubscription.NetworkSent -= NetworkSent;
                AutoSaveThread.CancelAsync();
                LogQueueWorker.CancelAsync();
                notifier.Stop();
                config.SaveSettings();
                SaveHousingList();
                logger.Close();
                logger = null;
                statusLabel.Text = "Exit :|";
            }
            else
            {
                statusLabel.Text = "Error :(";
            }
        }

        void IActPluginV1.InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            statusLabel = pluginStatusText;

            var plugins = ActGlobals.oFormActMain.ActPlugins;
            ActPluginData thisPlugin = null;
            foreach (var item in plugins)
            {
                if (item.pluginFile.Name.ToUpper().Contains("FFXIV_ACT_PLUGIN") && item.pluginObj != null)
                {
                    ffxivPlugin = new ACTPluginProxy(item.pluginObj);
                }
                if (item.pluginObj == this)
                {
                    thisPlugin = item;
                }
            }

            if (ffxivPlugin == null)
            {
                pluginStatusText.Text = "FFXIV Act Plugin is not loading.";
                return;
            }
            if (thisPlugin == null)
            {
                pluginStatusText.Text = "COULD NOT DETECT THIS PLUGIN DATA.";
                return;
            }

            config = new Config();
            config.LoadSettings();

            logger = new SimpleLoggerSync(Path.Combine(DataDir, "app.log"));
            logger.SetFilter(config.DebugEnabled ? LogLevel.DEBUG : LogLevel.INFO);
            storage = new DataStorage(logger);

            pluginScreenSpace.Text = "房屋信息记录";

            var vm = new PluginControlViewModel(config, logger, storage);
            var control = new PluginControlWpf();
            control.DataContext = vm;
            var host = new ElementHost()
            {
                Dock = DockStyle.Fill,
                Child = control
            };

            pluginScreenSpace.Controls.Add(host);

            notifier = new Notifier(config);
            api = new UploadApi(config);
            updater = new ExtendedUpdater("https://tools.lotlab.org/dl/ffxiv/HousingCheckXP/_update/", thisPlugin.pluginFile.DirectoryName);
            opcodeRepo = new OpcodeRepo(logger,
                Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Config", "HousingCheck.opcode.txt"),
                "https://tools.lotlab.org/dl/ffxiv/HousingCheckXP/opcode.txt"
            );

            updateOpcodeFromConfig();

            ffxivPlugin.DataSubscription.NetworkReceived += NetworkReceived;
            ffxivPlugin.DataSubscription.NetworkSent += NetworkSent;

            initialized = true;

            AutoSaveThread = new BackgroundWorker
            {
                WorkerSupportsCancellation = true
            };
            AutoSaveThread.DoWork += RunAutoUploadWorker;
            AutoSaveThread.RunWorkerAsync();

            LogQueueWorker = new BackgroundWorker
            {
                WorkerSupportsCancellation = true
            };
            LogQueueWorker.DoWork += RunLogQueueWorker;
            LogQueueWorker.RunWorkerAsync();

            notifier.Start();

            statusLabel.Text = "Working :D";

            vm.UploadManually.OnExecute += (obj) => { UploadOnce(); };
            vm.SaveToFile.OnExecute += (obj) => { SaveToCsv(); };
            vm.CopyToClipboard.OnExecute += (obj) => { CopySalesToClipboard(); };
            vm.TestNotification.OnExecute += (obj) => { notifier.NotifyEmptyHouseAsync(new HousingOnSaleItem(HouseArea.海雾村, 1, 2, HouseSize.L, 10000000, false)); };
            vm.CheckUpdate.OnExecute += (obj) => { CheckUpdate(); };
            vm.GetOnlineOpcode.OnExecute += async (obj) =>
            {
                logger.LogInfo("正在获取在线Opcode");
                try
                {
                    await opcodeRepo.GetOnlineOpcode();
                    opcodeRepo.SetOpcode(parser);
                    logger.LogInfo("在线Opcode获取成功");
                }
                catch (Exception e)
                {
                    logger.LogWarning("在线Opcode获取失败", e);
                }
            };

            vm.PropertyChanged += (object sender, PropertyChangedEventArgs e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(vm.UseCustomOpcode):
                        updateOpcodeFromConfig();
                        if (!config.UseCustomOpcode)
                            opcodeRepo.SetOpcode(parser);
                        break;
                }
            };

            PrepareDir();
            //恢复上次列表
            LoadHousingList();

            if (config.AutoUpdate)
            {
                CheckUpdate();
            }

            loadOpcode();
        }

        private void loadOpcode()
        {
            Task.Run(async () =>
            {
                try
                {
                    opcodeRepo.LoadLocalOpcode();
                }
                catch (Exception e)
                {
                    logger.LogError("本地Opcode载入失败", e);
                }

                if (config.AutoUpdate)
                {
                    var gameVersion = ffxivPlugin.DataRepository.GetGameVersion();
                    logger.LogInfo($"本地Opcode版本: {opcodeRepo.LocalVersion}，游戏版本: {gameVersion}");
                    if (opcodeRepo.LocalVersion != gameVersion)
                    {
                        logger.LogInfo("正在获取在线Opcode");
                        try
                        {
                            await opcodeRepo.GetOnlineOpcode();
                            logger.LogInfo($"在线Opcode获取成功！版本：{opcodeRepo.LocalVersion}");
                        }
                        catch (Exception e)
                        {
                            logger.LogWarning("在线Opcode获取失败", e);
                        }
                    }
                }

                opcodeRepo.SetOpcode(parser);
            });
        }

        private void updateOpcodeFromConfig()
        {
            parser.ClearOpcodes();

            parser.SetOpcode<HousingWardInfo>((ushort)config.OpcodeWard);
            parser.SetOpcode<LandInfoSign>((ushort)config.OpcodeLand);
            parser.SetOpcode<LandSaleInfo>((ushort)config.OpcodeSale);
            parser.SetOpcode<ClientTrigger>((ushort)config.OpcodeClientTrigger);
        }

        void WriteActLog(string message)
        {
            var logText = $"00|{DateTime.Now.ToString("O")}|0|HousingCheck-{message}|";        //解析插件数据格式化
            LogQueue.Add((DateTime.Now, logText));
        }

        void CheckUpdate()
        {
            updater.Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            Task.Run(async () =>
            {
                logger.LogInfo("正在检查更新...当前版本: " + updater.Version);
                try
                {
                    var updateInfo = await updater.CheckUpdateV2Async();
                    if (updateInfo == null)
                    {
                        logger.LogInfo("当前使用的是最新版本.");
                        return;
                    }

                    logger.LogInfo($"正在更新到新版本: {updateInfo.Value.Version} {updateInfo.Value.ChangeLog}");
                    try
                    {
                        await updater.UpdateAsync(updateInfo.Value);
                        logger.LogInfo("更新完毕，请重新加载插件或直接重启你的ACT。");

                    }
                    catch (Exception e)
                    {
                        logger.LogWarning("更新失败", e);
                    }
                }
                catch (Exception e)
                {
                    logger.LogWarning("检查更新失败", e);
                }
            });
        }

        void NetworkSent(string connection, long epoch, byte[] message)
        {
            try
            {
                var packet = parser.ParsePacket(message);
                switch (packet)
                {
                    case ClientTrigger trigger:
                        ClientTriggerParser(trigger, epoch);
                        break;
                    default:
                        break;
                }

                // 猜测 Opcode
                if (!config.EnableOpcodeGuess) return;

                var ipc = parser.ParseIPCHeader(message);
                if (ipc == null) return;
                var guessOpcode = ipc.Value.type;
                if (message.Length == Marshal.SizeOf<FFXIVIpcClientTrigger>())
                {
                    var trigger = parser.ParseAsPacket<ClientTrigger, FFXIVIpcClientTrigger>(message);
                    if (trigger.Value.commandId == 0x0451)
                    {
                        var req = parser.ParseAsPacket<ClientTriggerLandSaleRequest>(trigger.Value.data);
                        if (req.IsValid())
                        {
                            logger.LogDebug("ClientTrigger可能的Opcode为：" + guessOpcode);
                            logger.LogDebug(req.ToString());
                            if (config.DisableOpcodeCheck) ClientTriggerParser(trigger, epoch);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError("数据包解析失败", e);
                logger.LogDebug(message.ToHexString());
            }
        }

        void NetworkReceived(string connection, long epoch, byte[] message)
        {
            try
            {
                var packet = parser.ParsePacket(message);
                switch (packet)
                {
                    case HousingWardInfo ward:
                        WardInfoParser(ward, epoch);
                        break;
                    case LandInfoSign land:
                        LandInfoParser(land, epoch);
                        break;
                    case LandSaleInfo sale:
                        SaleInfoParser(sale, epoch);
                        break;
                    default:
                        break;
                }

                // 猜测 Opcode
                if (!config.EnableOpcodeGuess) return;

                var ipc = parser.ParseIPCHeader(message);
                if (ipc == null) return;

                var guessOpcode = ipc.Value.type;
                if (message.Length == Marshal.SizeOf<FFXIVIpcHousingWardInfo>())
                {
                    var wardInfo = parser.ParseAsPacket<HousingWardInfo, FFXIVIpcHousingWardInfo>(message);
                    if (wardInfo.IsValid())
                    {
                        logger.LogDebug("房屋列表可能的Opcode为：" + guessOpcode);
                        if (config.DisableOpcodeCheck) WardInfoParser(wardInfo, epoch);
                        return;
                    }
                }

                if (message.Length == Marshal.SizeOf<FFXIVIpcLandInfoSign>())
                {
                    var sign = parser.ParseAsPacket<LandInfoSign, FFXIVIpcLandInfoSign>(message);
                    if (sign.IsValid())
                    {
                        logger.LogDebug("房屋门牌可能的Opcode为：" + guessOpcode);
                        if (config.DisableOpcodeCheck) LandInfoParser(sign, epoch);
                        return;
                    }
                }

                if (message.Length == Marshal.SizeOf<FFXIVIpcLandSaleInfo>())
                {
                    var sale = parser.ParseAsPacket<LandSaleInfo, FFXIVIpcLandSaleInfo>(message);
                    if (sale.IsValid())
                    {
                        logger.LogDebug("房屋销售信息可能的Opcode为：" + guessOpcode);
                        logger.LogDebug(sale.ToString());
                        if (config.DisableOpcodeCheck) SaleInfoParser(sale, epoch);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError("数据包解析失败", e);
                logger.LogDebug(message.ToHexString());
            }
        }

        void WardInfoParser(HousingWardInfo info, Int64 epoch)
        {
            //解析数据包
            var snapshot = new HousingSlotSnapshot(info, epoch);

            var emptyHouses = storage.ProcessSnapshot(snapshot);
            foreach (var house in emptyHouses)
            {
                HousingOnSaleItem onSaleItem = new HousingOnSaleItem(house);
                var str = string.Format("{0} 第{1}区 {2}号 {3}房在售 当前价格: {4}",
                    onSaleItem.AreaStr, onSaleItem.DisplaySlot, onSaleItem.DisplayId,
                    onSaleItem.SizeStr, onSaleItem.Price);
                logger.LogInfo(str);
                WriteActLog(str);

                notifier.NotifyEmptyHouseAsync(onSaleItem);
            }

            // 输出翻页日志
            var logStr = string.Format("{0} 第{1}区查询完成",
                HousingItem.GetHouseAreaStr(snapshot.Area),
                snapshot.Slot + 1);
            logger.LogInfo(logStr);
            WriteActLog(logStr);
        }

        void LandInfoParser(LandInfoSign sign, long epoch)
        {
            var info = new HousingLandInfoSign(sign, epoch);
            storage.ProcessInfoSign(info);
        }

        void SaleInfoParser(LandSaleInfo sale, long epoch)
        {
            logger.LogDebug(sale.ToString());
            var lottery = storage.ProcessSaleInfo(sale, epoch);
            if (lottery != null)
            {
                logger.LogInfo(lottery.ToString());
            }
        }

        void ClientTriggerParser(ClientTrigger trigger, long epoch)
        {
            if (trigger.Value.commandId != 0x0451) return;
            var req = parser.ParseAsPacket<ClientTriggerLandSaleRequest>(trigger.Value.data);
            logger.LogDebug(req.ToString());

            var lottery = storage.ProcessLandSaleReq(req, GetServerID(), epoch);
            if (lottery != null)
            {
                logger.LogInfo(lottery.ToString());
            }
        }

        uint GetServerID()
        {
            return ffxivPlugin.DataRepository.GetCombatantList().FirstOrDefault(x => x.ID == ffxivPlugin.DataRepository.GetCurrentPlayerID()).CurrentWorldID;
        }

        private void UploadOnce()
        {
            logger.LogInfo($"准备上报");
            ManualUpload = true;
        }

        private void CopySalesToClipboard()
        {
            Clipboard.SetText(storage.GetSalesString(config.IgnoreEmpyreum));
            logger.LogInfo($"复制成功");
        }

        private void PrepareDir()
        {
            if (!Directory.Exists(SnapshotDir))
                Directory.CreateDirectory(SnapshotDir);
        }

        private void SaveHousingList()
        {
            try
            {
                storage.SaveSaleList(HousingListFile);
                logger.LogInfo($"房屋列表已保存到{HousingListFile}");
            }
            catch (Exception e)
            {
                logger.LogError("房屋列表保存失败", e);
            }
        }

        private void LoadHousingList()
        {
            if (!File.Exists(HousingListFile)) return;

            try
            {
                storage.LoadSaleList(HousingListFile);
                logger.LogInfo("已恢复上次保存的房屋列表");
            }
            catch (Exception e)
            {
                logger.LogError("恢复上次保存的房屋列表失败", e);
            }
        }

        string HousingListFile => Path.Combine(DataDir, "list.json");
        string DataDir => Path.Combine(Environment.CurrentDirectory, "AppData", "HousingCheck");
        string SnapshotDir => Path.Combine(DataDir, "snapshots");

        private void SaveToCsv()
        {
            PrepareDir();
            try
            {
                storage.ExportStorageToCsv(SnapshotDir);
                logger.LogInfo($"已保存到 {SnapshotDir} 文件夹");
            }
            catch (Exception e)
            {
                logger.LogError("保存失败", e);
            }
        }

        private void RunLogQueueWorker(object sender, DoWorkEventArgs e)
        {
            while (true)
            {
                if (LogQueueWorker.CancellationPending)
                {
                    break;
                }
                if (LogQueue.Count > 0)
                {
                    while (LogQueue.Count > 0)
                    {
                        var data = LogQueue.First();
                        LogQueue.RemoveAt(0);
                        ActGlobals.oFormActMain.ParseRawLogLine(false, data.Item1, data.Item2);
                    }
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
        }

        private void RunAutoUploadWorker(object sender, DoWorkEventArgs e)
        {
            while (!AutoSaveThread.CancellationPending)
            {
                try
                {
                    var actionTime = storage.LastActionTime + AutoSaveAfter;
                    bool uploadSnapshot = config.EnableUploadSnapshot;
                    ApiVersion apiVersion = config.UploadApiVersion;

                    bool shouldUpload = false;
                    bool saleListChanged = storage.SaleListChanged;

                    if (ManualUpload)
                    {
                        logger.LogDebug("开始手动上报任务");
                        shouldUpload = true;
                        ManualUpload = false;
                    }
                    else if (actionTime < DateTime.Now)
                    {
                        if (storage.SaleListChanged)
                        {
                            //保存列表文件
                            SaveHousingList();
                            logger.LogInfo("房屋信息已保存");
                            saleListChanged = false;
                        }

                        if (config.AutoUpload) shouldUpload = true;
                    }

                    if (shouldUpload)
                    {
                        if (apiVersion == ApiVersion.V1)
                        {
                            if (ManualUpload || storage.SaleListChanged)
                            {
                                UploadOnSaleList();
                            }
                            saleListChanged = false;
                        }

                        if (apiVersion == ApiVersion.V2 && uploadSnapshot)
                        {
                            if (storage.SnapshotChanged && storage.Snapshots.UploadCount > 0)
                                UploadSnapshot();

                            if (storage.InfoSignsChanged && storage.InfoSigns.UploadCount > 0)
                                UploadLandInfoSnapshot();

                            if (storage.LotteryInfoChanged && storage.Lotteries.UploadCount > 0)
                                UploadHouseLotteryList();
                        }
                    }

                    storage.SaleListChanged = saleListChanged;
                }
                catch (Exception ex)
                {
                    logger.LogError("执行定时任务时出现错误", ex);
                }

                Thread.Sleep(500);
            }

            logger.LogError("上报线程退出！！！！");
        }

        private void UploadOnSaleList()
        {
            try
            {
                var msg = storage.GetSalesString(config.IgnoreEmpyreum);
                if (msg.Length == 0)
                {
                    logger.LogError("上报数据为空");
                }
                try
                {
                    logger.LogInfo("正在上传房屋列表");
                    api.UploadOnSaleListMsg(msg);
                    logger.LogInfo("房屋列表上报成功");
                }
                catch (ServerResponseException e)
                {
                    logger.LogError("房屋列表上报出错。服务器返回：" + e.Message);
                }
                catch (Exception e)
                {
                    logger.LogError("房屋列表上报出错", e);
                }

                Thread.Sleep(1000);
            }
            catch (Exception e)
            {
                logger.LogError("房屋列表上报出错", e);
            }
        }

        private void UploadSnapshot()
        {
            try
            {
                List<HousingSlotSnapshotJSONObject> objs = new List<HousingSlotSnapshotJSONObject>();
                DateTime latest = DateTime.MinValue;
                foreach (var snapshot in storage.Snapshots.GetModifiedItems())
                {
                    if (snapshot == null) continue;
                    latest = snapshot.Time > latest ? snapshot.Time : latest;
                    objs.Add(snapshot.ToJsonObject());
                }

                try
                {
                    logger.LogInfo($"正在上传{objs.Count}个房区快照");
                    api.UploadHouselList(objs);
                    storage.Snapshots.MarkOutdated(latest);
                    storage.SnapshotChanged = false;
                    logger.LogInfo("房区快照上报成功");
                }
                catch (ServerResponseException e)
                {
                    logger.LogError("房区快照上报出错。服务器返回：" + e.Message);
                }
                catch (Exception e)
                {
                    logger.LogError("房区快照上报出错", e);
                }

                Thread.Sleep(1000);
            }
            catch (Exception e)
            {
                logger.LogError("房区快照上报出错", e);
            }
        }

        private void UploadLandInfoSnapshot()
        {
            try
            {
                List<LandInfoSignBrief> objs = new List<LandInfoSignBrief>();
                DateTime generatedTime = DateTime.MinValue;
                foreach (var snapshot in storage.InfoSigns.GetModifiedItems())
                {
                    if (snapshot == null) continue;
                    generatedTime = snapshot.Time > generatedTime ? snapshot.Time : generatedTime;
                    objs.Add(new LandInfoSignBrief(snapshot));
                }

                try
                {
                    logger.LogInfo($"正在上传{objs.Count}间房屋详细信息");
                    api.UploadDetailList(objs);
                    logger.LogInfo("房屋详细信息上报成功");
                    storage.InfoSigns.MarkOutdated(generatedTime);
                    storage.InfoSignsChanged = false;
                }
                catch (ServerResponseException e)
                {
                    logger.LogError("房屋详细信息上报出错。服务器返回：" + e.Message);
                }
                catch (Exception e)
                {
                    logger.LogError("房屋详细信息上报出错", e);
                }

                Thread.Sleep(1000);
            }
            catch (Exception e)
            {
                logger.LogError("房屋详细信息上报出错", e);
            }
        }

        private void UploadHouseLotteryList()
        {
            try
            {
                var objs = new List<HousingLotteryInfoBrief>();
                DateTime generatedTime = DateTime.MinValue;
                foreach (var snapshot in storage.Lotteries.GetModifiedItems())
                {
                    if (snapshot == null) continue;
                    generatedTime = snapshot.Time > generatedTime ? snapshot.Time : generatedTime;
                    objs.Add(new HousingLotteryInfoBrief(snapshot));
                }

                try
                {
                    logger.LogInfo($"正在上传{objs.Count}间房屋售卖信息");
                    api.UploadLotteryList(objs);
                    logger.LogInfo("房屋售卖信息上报成功");
                    storage.Lotteries.MarkOutdated(generatedTime);
                    storage.LotteryInfoChanged = false;
                }
                catch (ServerResponseException e)
                {
                    logger.LogError("房屋售卖信息上报出错。服务器返回：" + e.Message);
                }
                catch (Exception e)
                {
                    logger.LogError("房屋售卖信息上报出错", e);
                }

                Thread.Sleep(1000);
            }
            catch (Exception e)
            {
                logger.LogError("房屋售卖信息上报出错", e);
            }
        }
    }


    static class PacketValidator
    {
        public static bool IsValid(this HousingWardInfo info)
        {
            if (LandIdent.GetHouseArea(info.Value.landIdent.territoryTypeId) == HouseArea.UNKNOW) return false;
            if (info.Value.landIdent.wardNum >= 24) return false;
            for (int i = 0; i < 4; i++)
                if (info.Value.purchaseType[i] >= 4) return false;
            return true;
        }

        public static bool IsValid(this LandInfoSign info)
        {
            if (LandIdent.GetHouseArea(info.Value.landIdent.territoryTypeId) == HouseArea.UNKNOW) return false;
            if (info.Value.landIdent.landId >= 60) return false;
            if (info.Value.landIdent.wardNum >= 24) return false;
            if (info.Value.houseSize > (int)HouseSize.L) return false;
            return true;
        }

        public static bool IsValid(this LandSaleInfo info)
        {
            if ((int)info.Value.purchase_type >= 3) return false;
            if ((int)info.Value.region_type >= 3) return false;
            if ((int)info.Value.status >= 4) return false;

            var time = DateTimeOffset.Now.ToUnixTimeSeconds();
            // 结束时间必须是整点，且必须大于当前时间
            if (info.Value.endTime % 3600 != 0 || info.Value.endTime < time) return false;

            return true;
        }
    }
}
