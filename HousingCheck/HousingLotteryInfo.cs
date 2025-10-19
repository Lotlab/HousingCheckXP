using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace HousingCheck
{
    public class HousingLotteryInfo
    {
        public LandIdent LandIdent { get; }

        public int ServerId => LandIdent.ServerID;
        public HouseArea Area => LandIdent.Area;
        public int Slot => LandIdent.Slot;
        public int LandID => LandIdent.LandID;

        public HousePurchaseType PurchaseType { get; }
        public HouseRegionType RegionType { get; }
        public LandStatus Status { get; }

        public DateTime Time { get; }
        public UInt32 EndTime { get; }
        public UInt32 Persons { get; }
        public UInt32 Winner { get; }

        public HousingLotteryInfo(LandIdent ident, LandSaleInfo saleInfo, Int64 epoch)
        {
            LandIdent = ident;

            Time = DateTimeOffset.FromUnixTimeSeconds(epoch / 1000).LocalDateTime;
            PurchaseType = saleInfo.Value.purchase_type;
            RegionType = saleInfo.Value.region_type;
            Status = saleInfo.Value.status;
            EndTime = saleInfo.Value.endTime;
            Persons = saleInfo.Value.persons;
            Winner = saleInfo.Value.winner;
        }

        public override string ToString()
        {
            return $"{LandIdent} {Status.GetDesc()}, 当前人数 {Persons}, 截止时间 {DateTimeOffset.FromUnixTimeSeconds(EndTime).LocalDateTime}";
        }
    }

    public class HousingLotteryInfoBrief
    {
        public int ServerId { get; }
        public int Area { get; }
        /// <summary>
        /// 从0开始
        /// </summary>
        public int Slot { get; }
        /// <summary>
        /// 从1开始
        /// </summary>
        public int LandID { get; }

        public Int64 Time { get; }
        public UInt32 EndTime { get; }
        public int State { get; }
        public UInt32 Participate { get; }
        public UInt32 Winner { get; }

        public HousingLotteryInfoBrief(HousingLotteryInfo sign)
        {
            ServerId = sign.ServerId;
            Area = HousingItem.GetHouseAreaNum(sign.Area);
            Slot = sign.Slot;
            LandID = sign.LandID + 1;
            Time = new DateTimeOffset(sign.Time).ToUnixTimeSeconds();

            State = (int)sign.Status;
            Participate = sign.Persons;
            Winner = sign.Winner;
            EndTime = sign.EndTime;
        }
    }

    public class HousingLotteryInfoStorage
    {
        Dictionary<LandIdent, HousingLotteryInfo> storage = new Dictionary<LandIdent, HousingLotteryInfo>();

        DateTime timeAfter = DateTime.Now;

        public void Add(HousingLotteryInfo info)
        {
            lock (this)
            {
                storage[info.LandIdent] = info;
            }
        }

        public void Clear()
        {
            storage.Clear();
        }

        public void MarkOutdated(DateTime date)
        {
            timeAfter = date;
        }

        public int Count => storage.Count;

        public int UploadCount => storage.Where(i => i.Value.Time > timeAfter).Count();

        public void WriteCSV(StreamWriter writer)
        {
            var values = storage.OrderBy(a => a.Key).Select(a => a.Value);
            writer.WriteLine("服务器,地区,房区,房号,类型,限制,状态,结束时间,更新时间");
            foreach (var item in values)
            {
                writer.WriteLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                    item.ServerId,
                    item.Area,
                    item.Slot + 1,
                    item.LandID + 1,
                    item.PurchaseType.GetDesc(),
                    item.RegionType.GetDesc(),
                    item.Status.GetDesc(),
                    item.EndTime,
                    item.Time)
                );
            }
        }


        public IEnumerable<HousingLotteryInfo> GetModifiedItems()
        {
            lock (this)
            {
                return storage.Where(kv => kv.Value.Time > timeAfter).Select(kv => kv.Value);
            }
        }
    }
}