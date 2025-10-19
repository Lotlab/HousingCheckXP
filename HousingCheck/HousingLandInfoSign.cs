using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lotlab.PluginCommon.FFXIV.Parser.Packets;
using Lotlab.PluginCommon.FFXIV.Parser;

namespace HousingCheck
{
    public class HousingLandInfoSign
    {
        public LandIdent LandIdent { get; }
        public int ServerId => LandIdent.ServerID;
        public HouseArea Area => LandIdent.Area;
        public int Slot => LandIdent.Slot;
        public int LandID => LandIdent.LandID;

        public DateTime Time { get; }
        public UInt64 OwnerID { get; }
        public byte HouseIconAdd { get; }
        public HouseSize HouseSize { get; }
        public HouseOwnerType HouseType { get; }
        public string EstateName { get; }
        public string EstateGreeting { get; }
        public string OwnerName { get; }
        public string FcTag { get; }
        public byte[] Tag { get; }

        public HousingLandInfoSign(LandInfoSign sign, Int64 epoch)
        {
            Time = DateTimeOffset.FromUnixTimeSeconds(epoch / 1000).LocalDateTime;
            LandIdent = new LandIdent(sign.Value.landIdent);

            OwnerID = sign.Value.ownerId;

            HouseIconAdd = sign.Value.houseIconAdd; // 01: ?
            HouseSize = (HouseSize)sign.Value.houseSize;// 00: S, 01: M, 02: L
            HouseType = sign.Value.houseType == 2 ? HouseOwnerType.PERSON : HouseOwnerType.GUILD; // 00: FC, 02: Personal

            EstateName = sign.Value.estateName.GetUTF8String();
            EstateGreeting = sign.Value.estateGreeting.GetUTF8String();
            OwnerName = sign.Value.ownerName.GetUTF8String();
            FcTag = sign.Value.fcTag.GetUTF8String();
            Tag = sign.Value.tag;
        }

        /// <summary>
        /// 创建一个空房
        /// </summary>
        /// <param name="serverID"></param>
        /// <param name="area"></param>
        /// <param name="slot"></param>
        /// <param name="id"></param>
        /// <param name="time"></param>
        /// <param name="size"></param>
        public HousingLandInfoSign(int serverID, HouseArea area, int slot, int id, DateTime time, HouseSize size) :
            this(new LandIdent(serverID, area, slot, id), time, size)
        {
        }

        /// <summary>
        /// 创建一个空房
        /// </summary>
        /// <param name="land"></param>
        /// <param name="time"></param>
        public HousingLandInfoSign(LandIdent land, DateTime time, HouseSize size)
        {
            LandIdent = land;
            Time = time;
            HouseSize = size;
            OwnerID = 0;
            HouseType = HouseOwnerType.EMPTY;
        }
    }

    public class LandInfoSignBrief
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
        public UInt64 OwnerID { get; }
        public string OwnerName { get; }
        public string FcTag { get; }
        public bool IsPersonal { get; }

        public LandInfoSignBrief(HousingLandInfoSign sign)
        {
            ServerId = sign.ServerId;
            Area = HousingItem.GetHouseAreaNum(sign.Area);
            Slot = sign.Slot;
            LandID = sign.LandID + 1;
            Time = new DateTimeOffset(sign.Time).ToUnixTimeSeconds();
            OwnerID = sign.OwnerID;
            OwnerName = sign.OwnerName;
            FcTag = sign.FcTag;
            IsPersonal = sign.HouseType == HouseOwnerType.PERSON;
        }
    }

    public class LandInfoSignStorage
    {
        Dictionary<LandIdent, HousingLandInfoSign> storage = new Dictionary<LandIdent, HousingLandInfoSign>();

        DateTime timeAfter = DateTime.Now;

        public void Add(HousingLandInfoSign info)
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
            writer.WriteLine("服务器,地区,房区,房号,大小,类型,所有者ID,所有者名称,部队简称,房屋名称,房屋问候语,更新时间");
            foreach (var item in values)
            {
                writer.WriteLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11}",
                    item.ServerId,
                    item.Area,
                    item.Slot + 1,
                    item.LandID + 1,
                    item.HouseSize,
                    HousingItem.GetOwnerTypeStr(item.HouseType),
                    item.OwnerID,
                    item.OwnerName,
                    item.FcTag,
                    item.EstateName,
                    item.EstateGreeting?.Replace("\r", "\\n"), // 描述内换行替换
                    item.Time)
                );
            }
        }


        public IEnumerable<HousingLandInfoSign> GetModifiedItems()
        {
            lock (this)
            {
                return storage.Where(kv => kv.Value.Time > timeAfter).Select(kv => kv.Value);
            }
        }
    }
}
