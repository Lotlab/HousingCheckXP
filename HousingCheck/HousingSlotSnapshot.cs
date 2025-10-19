using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections;
using Newtonsoft.Json;
using Lotlab.PluginCommon.FFXIV.Parser.Packets;

namespace HousingCheck
{
    public class HousingSlotSnapshotJSONObject
    {
        public long time;
        public int server;
        public string area;
        public int slot;
        public int purchase_main;
        public int purchase_sub;
        public int region_main;
        public int region_sub;

        public HousingItemJSONObject[] houses;
    }

    public class LandIdent : IEquatable<LandIdent>, IComparable<LandIdent>
    {
        /// <summary>
        /// 服务器ID
        /// worldId
        /// </summary>
        public int ServerID { get; }
        /// <summary>
        /// 房屋区域
        /// territoryTypeId
        /// </summary>
        public HouseArea Area { get; } = HouseArea.UNKNOW;
        /// <summary>
        /// 小分区
        /// wardNum，从0开始
        /// </summary>
        public int Slot { get; }
        /// <summary>
        /// 房屋ID
        /// landID，从0开始
        /// </summary>
        public int LandID { get; } = 0;

        public LandIdent(Lotlab.PluginCommon.FFXIV.Parser.Packets.LandIdent ident)
        {
            ServerID = ident.worldId;
            Area = GetHouseArea(ident.territoryTypeId);
            Slot = ident.wardNum;
            LandID = ident.landId;
        }

        public LandIdent(int server, HouseArea area, int slot, int land = 0)
        {
            ServerID = server;
            Area = area;
            Slot = slot;
            LandID = land;
        }

        public LandIdent(LandIdent ident, int landID = 0)
        {
            ServerID = ident.ServerID;
            Area = ident.Area;
            Slot = ident.Slot;
            LandID = landID;
        }

        bool IEquatable<LandIdent>.Equals(LandIdent other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            return ServerID == other.ServerID && Area == other.Area && Slot == other.Slot && LandID == other.LandID;
        }

        public override int GetHashCode()
        {
            return (ServerID << 6) + ((int)Area << 4) + (Slot << 2) + LandID;
        }

        int IComparable<LandIdent>.CompareTo(LandIdent other)
        {
            int tmp = ServerID - other.ServerID;
            if (tmp != 0) return Math.Sign(tmp);

            tmp = Area - other.Area;
            if (tmp != 0) return Math.Sign(tmp);

            tmp = Slot - other.Slot;
            if (tmp != 0) return Math.Sign(tmp);

            return Math.Sign(LandID - other.LandID);
        }

        public override string ToString()
        {
            return $"{Area} {Slot + 1}-{LandID + 1}";
        }

        public static HouseArea GetHouseArea(UInt16 territoryTypeId)
        {
            switch (territoryTypeId)
            {
                case 0x153:
                    return HouseArea.海雾村;
                case 0x154:
                    return HouseArea.薰衣草苗圃;
                case 0x155:
                    return HouseArea.高脚孤丘;
                case 0x281:
                    return HouseArea.白银乡;
                case 0x3D3:
                    return HouseArea.穹顶皓天;
                default:
                    return HouseArea.UNKNOW;
            }
        }
    }

    public class HousingSlotSnapshot
    {
        public DateTime Time { get; }
        public int ServerId => landIdent.ServerID;
        public HouseArea Area => landIdent.Area;
        public int Slot => landIdent.Slot;
        public LandIdent landIdent { get; }
        public HousePurchaseType PurchaseTypeMain { get; }
        public HousePurchaseType PurchaseTypeSub { get; }
        public HouseRegionType RegionFlagMain { get; }
        public HouseRegionType RegionFlagSub { get; }

        public SortedList<int, HousingItem> HouseList = new SortedList<int, HousingItem>();

        public HousingSlotSnapshot(HousingWardInfo info, Int64 epoch)
        {
            Time = DateTimeOffset.FromUnixTimeSeconds(epoch / 1000).LocalDateTime;
            landIdent = new LandIdent(info.Value.landIdent);

            var purchaseType = info.Value.purchaseType;
            PurchaseTypeMain = (HousePurchaseType)purchaseType[0];
            PurchaseTypeSub = (HousePurchaseType)purchaseType[1];
            RegionFlagMain = (HouseRegionType)purchaseType[2];
            RegionFlagSub = (HouseRegionType)purchaseType[3];

            for (int i = 0; i < info.Value.houseInfoEntry.Length; i++)
            {
                HousingItem house = new HousingItem(new LandIdent(landIdent, i), info.Value.houseInfoEntry[i]);
                HouseList.Add(i, house);
            }
        }

        public HousingItem[] GetOnSale()
        {
            List<HousingItem> onSaleList = new List<HousingItem>();
            foreach (HousingItem house in HouseList.Values)
            {
                if (house.IsEmpty)
                {
                    onSaleList.Add(house);
                }
            }
            return onSaleList.ToArray();
        }

        public string ToCsv()
        {
            StringBuilder csv = new StringBuilder();
            foreach (var house in HouseList.Values)
            {
                csv.AppendLine(house.ToCsvLine(house.Id < 30 ? PurchaseTypeMain : PurchaseTypeSub, house.Id < 30 ? RegionFlagMain : RegionFlagSub));
            }
            csv.AppendLine();
            return csv.ToString();
        }

        public string SlotStateCsv()
        {
            return string.Join(",", new string[] {
                HousingItem.GetHouseAreaStr(Area),
                (Slot + 1).ToString(),
                PurchaseTypeMain.GetDesc(),
                PurchaseTypeSub.GetDesc(),
                RegionFlagMain.GetDesc(),
                RegionFlagSub.GetDesc(),
            });
        }

        public HousingSlotSnapshotJSONObject ToJsonObject()
        {
            HousingSlotSnapshotJSONObject ret = new HousingSlotSnapshotJSONObject();
            ret.area = HousingItem.GetHouseAreaStr(Area);
            ret.server = ServerId;
            ret.slot = Slot;
            ret.time = new DateTimeOffset(Time).ToUnixTimeSeconds();

            ret.purchase_main = (int)PurchaseTypeMain;
            ret.purchase_sub = (int)PurchaseTypeSub;
            ret.region_main = (int)RegionFlagMain;
            ret.region_sub = (int)RegionFlagSub;

            List<HousingItemJSONObject> houseListJson = new List<HousingItemJSONObject>();
            foreach (var house in HouseList.Values)
            {
                houseListJson.Add(house.ToJsonObject());
            }

            ret.houses = houseListJson.ToArray();

            return ret;
        }
    }
}
