using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Linq;
using Newtonsoft.Json;
using System.IO;
using System.Text;
using Lotlab.PluginCommon;

namespace HousingCheck
{
    public class DataStorage
    {
        /// <summary>
        /// 房区快照存储
        /// </summary>
        public HousingSnapshotStorage Snapshots { get; } = new HousingSnapshotStorage();

        /// <summary>
        /// 房屋详细信息存储
        /// </summary>
        public LandInfoSignStorage InfoSigns { get; } = new LandInfoSignStorage();

        /// <summary>
        /// 房屋抽签信息存储
        /// </summary>
        /// <returns></returns>
        public HousingLotteryInfoStorage Lotteries { get; } = new HousingLotteryInfoStorage();

        /// <summary>
        /// 正在出售房屋列表
        /// </summary>
        /// <typeparam name="HousingOnSaleItem"></typeparam>
        /// <returns></returns>
        public ObservableCollection<HousingOnSaleItem> Sales { get; } = new ObservableCollection<HousingOnSaleItem>();

        readonly object salesLock = new object();

        /// <summary>
        /// 上次动作时间
        /// </summary>
        /// <value></value>
        public DateTime LastActionTime { get; private set; } = DateTime.Now;

        /// <summary>
        /// 出售列表是否有变动
        /// </summary>
        /// <value></value>
        public bool SaleListChanged { get; set; } = false;

        public bool SnapshotChanged { get; set; } = false;

        public bool InfoSignsChanged { get; set; } = false;

        public bool LotteryInfoChanged { get; set; } = false;

        Tuple<ClientTriggerLandSaleRequest, DateTime, uint> lastLotteryInfoRequest = null;
        Tuple<LandSaleInfo, DateTime> lastLotteryInfo = null;
        HousingSlotSnapshot lastSnapshot = null;

        SimpleLogger logger { get; }
        public DataStorage(SimpleLogger logger)
        {
            this.logger = logger;
            BindingOperations.EnableCollectionSynchronization(Sales, salesLock);
        }

        /// <summary>
        /// 保存正在出售列表
        /// </summary>
        /// <param name="path"></param>
        public void SaveSaleList(string path)
        {
            var json = JsonConvert.SerializeObject(Sales.Where(s => s.CurrentStatus));
            using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                writer.Write(json);
            }
        }

        /// <summary>
        /// 载入正在出售列表
        /// </summary>
        /// <param name="path"></param>
        public void LoadSaleList(string path)
        {
            using (StreamReader reader = new StreamReader(path, Encoding.UTF8))
            {
                string jsonStr = reader.ReadToEnd();
                var list = JsonConvert.DeserializeObject<HousingOnSaleItem[]>(jsonStr);
                UpdateSales(list);
            }
        }

        /// <summary>
        /// 更新在售信息
        /// 
        /// 若当前不在出售列表，则会添加一个新的
        /// </summary>
        /// <param name="items"></param>
        public void UpdateSales(IEnumerable<HousingOnSaleItem> items)
        {
            lock (salesLock)
            {
                foreach (HousingOnSaleItem item in items)
                {
                    int listIndex;
                    if ((listIndex = Sales.IndexOf(item)) != -1)
                    {
                        Sales[listIndex].Update(item);
                    }
                    else
                    {
                        Sales.Add(item);
                    }
                }
            }
        }

        /// <summary>
        /// 移除在售信息
        /// </summary>
        /// <param name="items"></param>
        public void RemoveSales(IEnumerable<HousingOnSaleItem> items)
        {
            lock (salesLock)
            {
                foreach (var item in items)
                {
                    if (Sales.Contains(item))
                    {
                        Sales.Remove(item);
                    }
                }
            }
        }

        /// <summary>
        /// 获取人类可读的在售信息列表字符串
        /// </summary>
        /// <param name="IgnoreEmpyreum">是否忽略穹顶昊天</param>
        /// <returns></returns>
        public string GetSalesString(bool IgnoreEmpyreum)
        {
            byte area = 0;
            StringBuilder stringBuilder = new StringBuilder();

            lock (salesLock)
            {
                foreach (var line in Sales)
                {
                    if (!line.CurrentStatus)
                        continue;

                    if (line.Area == HouseArea.穹顶皓天 && IgnoreEmpyreum)
                        continue;

                    stringBuilder.Append($"{line.AreaStr} 第{line.DisplaySlot}区 {line.DisplayId}号{line.SizeStr}房在售，当前价格:{line.Price} {Environment.NewLine}");

                    if (line.Area >= 0) area |= (byte)(1 << (int)line.Area);
                }
            }

            for (int i = 1; i <= (int)HouseArea.穹顶皓天; i++)
            {
                if ((area & (1 << i)) == 0)
                {
                    stringBuilder.Append($"{HousingItem.GetHouseAreaStr((HouseArea)i)} 无空房 {Environment.NewLine}");
                }
            }

            return stringBuilder.ToString();
        }

        /// <summary>
        /// 将存储的快照数据写出到指定文件夹
        /// </summary>
        /// <param name="dir"></param>
        public void ExportStorageToCsv(string dir)
        {
            string time = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string snapshotFilename = $"HousingCheck-{time}-Snapshot.csv";
            string landInfoFilename = $"HousingCheck-{time}-LandInfo.csv";

            using (var writer = new StreamWriter(Path.Combine(dir, snapshotFilename), false, Encoding.UTF8))
            {
                Snapshots.SaveCsv(writer);
            }

            if (InfoSigns.Count > 0)
            {
                using (var writer = new StreamWriter(Path.Combine(dir, landInfoFilename), false, Encoding.UTF8))
                {
                    InfoSigns.WriteCSV(writer);
                }
            }
        }

        public IEnumerable<HousingItem> ProcessSnapshot(HousingSlotSnapshot snapshot)
        {
            if (snapshot.ServerId == 0) return new HousingItem[0];

            var lastSnapshot = Snapshots.Get(snapshot.landIdent);
            Snapshots.Add(snapshot);
            lastSnapshot = snapshot;

            var updateList = new List<HousingOnSaleItem>();
            var removeList = new List<HousingOnSaleItem>();
            var emptyList = new List<HousingItem>();

            foreach (var item in snapshot.HouseList)
            {
                var house = item.Value;
                bool isExists = false;

                // 检查是否之前是否正在出售
                lock (salesLock)
                {
                    var oldSales = Sales.Where(s => s.Area == house.Area && s.Slot == house.Slot && s.Id == house.Id);
                    foreach (var oldOnSaleItem in oldSales)
                    {
                        isExists = true;
                    }
                }

                if (isExists != house.IsEmpty)
                {
                    SaleListChanged = true;
                }

                if (house.IsEmpty)
                {
                    emptyList.Add(house);
                    updateList.Add(new HousingOnSaleItem(house));

                    var signInfo = new HousingLandInfoSign(snapshot.ServerId, house.Area, house.Slot, house.Id, snapshot.Time, house.Size);
                    InfoSigns.Add(signInfo);
                    InfoSignsChanged = true;
                }
                else if (isExists)
                {
                    removeList.Add(new HousingOnSaleItem(house));
                }
            }

            UpdateSales(updateList);
            RemoveSales(removeList);

            LastActionTime = DateTime.Now;
            SnapshotChanged = true;
            return emptyList;
        }

        public void ProcessInfoSign(HousingLandInfoSign sign)
        {
            InfoSigns.Add(sign);
            InfoSignsChanged = true;
            LastActionTime = DateTime.Now;
        }

        readonly object lotteryLock = new object();

        public HousingLotteryInfo ProcessLandSaleReq(ClientTriggerLandSaleRequest req, uint currentServer, long epoch)
        {
            lock (lotteryLock)
            {
                var time = DateTime.Now;
                if (lastLotteryInfo != null)
                {
                    if ((time - lastLotteryInfo.Item2).Duration() <= TimeSpan.FromSeconds(1))
                    {
                        var info = ProcessLotteryInfo(req, lastLotteryInfo.Item1, currentServer, epoch);
                        lastLotteryInfo = null;
                        return info;
                    } 
                    else
                    {
                        logger.LogDebug($"time: {time}, last: {lastLotteryInfo.Item2}");
                    }
                    //lastLotteryInfo = null;
                } else
                {
                    logger.LogDebug("lastLotteryInfo is null!");
                }

                lastLotteryInfoRequest = new Tuple<ClientTriggerLandSaleRequest, DateTime, uint>(req, time, currentServer);
            }
            return null;
        }

        public HousingLotteryInfo ProcessSaleInfo(LandSaleInfo info, Int64 epoch)
        {
            lock (lotteryLock)
            {
                var time = DateTime.Now;
                if (lastLotteryInfoRequest != null)
                {
                    if ((time - lastLotteryInfoRequest.Item2).Duration() <= TimeSpan.FromSeconds(1))
                    {
                        var lotteryInfo = ProcessLotteryInfo(lastLotteryInfoRequest.Item1, info, lastLotteryInfoRequest.Item3, epoch);
                        lastLotteryInfoRequest = null;
                        return lotteryInfo;
                    }
                    else
                    {
                        logger.LogDebug($"time: {epoch}, last: {lastLotteryInfoRequest.Item2}");
                    }
                    //lastLotteryInfoRequest = null;
                }
                else
                {
                    logger.LogDebug("lastLotteryInfoRequest is null!");
                }

                lastLotteryInfo = new Tuple<LandSaleInfo, DateTime>(info, time);
            }
            return null;
        }

        HousingLotteryInfo ProcessLotteryInfo(ClientTriggerLandSaleRequest req, LandSaleInfo info, uint serverID, long epoch)
        {
            // todo: get current server ID
            if (serverID == 0 && lastSnapshot != null) serverID = (uint)lastSnapshot.ServerId;

            LandIdent ident = new LandIdent(new Lotlab.PluginCommon.FFXIV.Parser.Packets.LandIdent()
            {
                landId = req.landId,
                wardNum = req.wardNum,
                territoryTypeId = req.territoryTypeId,
                worldId = (UInt16)serverID,
            });

            var lotteryInfo = new HousingLotteryInfo(ident, info, epoch);
            Lotteries.Add(lotteryInfo);
            LotteryInfoChanged = true;
            LastActionTime = DateTime.Now;

            return lotteryInfo;
        }
    }
}