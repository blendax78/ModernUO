using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Server.ContextMenus;
using Server.Engines.BulkOrders;
using Server.Ethics;
using Server.Gumps;
using Server.Items;
using Server.Misc;
using Server.Multis;
using Server.Prompts;
using Server.Targeting;

namespace Server.Mobiles
{
    [AttributeUsage(AttributeTargets.Class)]
    public class PlayerVendorTargetAttribute : Attribute
    {
    }

    public class VendorItem
    {
        private string m_Description;

        public VendorItem(Item item, int price, string description, DateTime created)
        {
            Item = item;
            Price = price;
            m_Description = description ?? "";
            Created = created;
            Valid = true;
        }

        public Item Item { get; }

        public int Price { get; }

        public string FormattedPrice =>
            Core.ML ? Price.ToString("N0", CultureInfo.GetCultureInfo("en-US")) : Price.ToString();

        public string Description
        {
            get => m_Description;
            set
            {
                m_Description = value ?? "";

                if (Valid)
                {
                    Item.InvalidateProperties();
                }
            }
        }

        public DateTime Created { get; }

        public bool IsForSale => Price >= 0;
        public bool IsForFree => Price == 0;

        public bool Valid { get; private set; }

        public void Invalidate()
        {
            Valid = false;
        }
    }

    public class VendorBackpack : Backpack
    {
        public VendorBackpack()
        {
            Layer = Layer.Backpack;
            Weight = 1.0;
        }

        public VendorBackpack(Serial serial) : base(serial)
        {
        }

        public override int DefaultMaxWeight => 0;

        public override bool CheckHold(Mobile m, Item item, bool message, bool checkItems, int plusItems, int plusWeight)
        {
            if (!base.CheckHold(m, item, message, checkItems, plusItems, plusWeight))
            {
                return false;
            }

            if (Ethic.IsImbued(item, true))
            {
                if (message)
                {
                    m.SendMessage("Imbued items may not be sold here.");
                }

                return false;
            }

            if (!BaseHouse.NewVendorSystem && Parent is PlayerVendor vendor)
            {
                var house = vendor.House;

                if (house?.IsAosRules == true && !house.CheckAosStorage(1 + item.TotalItems + plusItems))
                {
                    if (message)
                    {
                        m.SendLocalizedMessage(1061839); // This action would exceed the secure storage limit of the house.
                    }

                    return false;
                }
            }

            return true;
        }

        public override bool IsAccessibleTo(Mobile m) => true;

        public override bool CheckItemUse(Mobile from, Item item)
        {
            if (!base.CheckItemUse(from, item))
            {
                return false;
            }

            if (item is Container or BulkOrderBook)
            {
                return true;
            }

            from.SendLocalizedMessage(500447); // That is not accessible.
            return false;
        }

        public override bool CheckTarget(Mobile from, Target targ, object targeted) =>
            base.CheckTarget(from, targ, targeted) &&
            (from.AccessLevel >= AccessLevel.GameMaster ||
             targ.GetType().IsDefined(typeof(PlayerVendorTargetAttribute), false));

        public override void GetChildContextMenuEntries(Mobile from, List<ContextMenuEntry> list, Item item)
        {
            base.GetChildContextMenuEntries(from, list, item);

            if (RootParent is not PlayerVendor pv || pv.IsOwner(from))
            {
                return;
            }

            var vi = pv.GetVendorItem(item);

            if (vi != null)
            {
                list.Add(new BuyEntry(item));
            }
        }

        public override void GetChildNameProperties(IPropertyList list, Item item)
        {
            base.GetChildNameProperties(list, item);

            var pv = RootParent as PlayerVendor;

            var vi = pv?.GetVendorItem(item);

            if (vi == null)
            {
                return;
            }

            if (!vi.IsForSale)
            {
                list.Add(1043307); // Price: Not for sale.
            }
            else if (vi.IsForFree)
            {
                list.Add(1043306); // Price: FREE!
            }
            else
            {
                list.Add(1043304, vi.FormattedPrice); // Price: ~1_COST~
            }
        }

        public override void GetChildProperties(IPropertyList list, Item item)
        {
            base.GetChildProperties(list, item);

            var pv = RootParent as PlayerVendor;

            var vi = pv?.GetVendorItem(item);

            if (vi?.Description?.Length > 0)
            {
                list.Add(1043305, vi.Description); // <br>Seller's Description:<br>"~1_DESC~"
            }
        }

        public override void OnSingleClickContained(Mobile from, Item item)
        {
            if (RootParent is PlayerVendor vendor)
            {
                var vi = vendor.GetVendorItem(item);

                if (vi != null)
                {
                    if (!vi.IsForSale)
                    {
                        item.LabelTo(from, 1043307); // Price: Not for sale.
                    }
                    else if (vi.IsForFree)
                    {
                        item.LabelTo(from, 1043306); // Price: FREE!
                    }
                    else
                    {
                        item.LabelTo(from, 1043304, vi.FormattedPrice); // Price: ~1_COST~
                    }

                    if (!string.IsNullOrEmpty(vi.Description))
                    {
                        item.LabelTo(from, $"Description: {vi.Description}");
                    }
                }
            }

            base.OnSingleClickContained(from, item);
        }

        public override void Serialize(IGenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write(0); // version
        }

        public override void Deserialize(IGenericReader reader)
        {
            base.Deserialize(reader);

            var version = reader.ReadInt();
        }

        private class BuyEntry : ContextMenuEntry
        {
            private readonly Item m_Item;

            public BuyEntry(Item item) : base(6103) => m_Item = item;

            public override bool NonLocalUse => true;

            public override void OnClick()
            {
                if (m_Item.Deleted)
                {
                    return;
                }

                PlayerVendor.TryToBuy(m_Item, Owner.From);
            }
        }
    }

    public class PlayerVendor : Mobile
    {
        private BaseHouse m_House;

        private Timer m_PayTimer;
        private Dictionary<Item, VendorItem> m_SellItems;

        private string m_ShopName;

        public PlayerVendor(Mobile owner, BaseHouse house)
        {
            Owner = owner;
            House = house;

            if (BaseHouse.NewVendorSystem)
            {
                BankAccount = 0;
                HoldGold = 4;
            }
            else
            {
                BankAccount = 1000;
                HoldGold = 0;
            }

            ShopName = "Shop Not Yet Named";

            m_SellItems = new Dictionary<Item, VendorItem>();

            CantWalk = true;

            if (!Core.AOS)
            {
                NameHue = 0x35;
            }

            InitStats(100, 100, 25);
            InitBody();
            InitOutfit();

            var delay = PayTimer.GetInterval();

            m_PayTimer = new PayTimer(this, delay);
            m_PayTimer.Start();

            NextPayTime = Core.Now + delay;
        }

        public PlayerVendor(Serial serial) : base(serial)
        {
        }

        [CommandProperty(AccessLevel.GameMaster)]
        public Mobile Owner { get; set; }

        [CommandProperty(AccessLevel.GameMaster)]
        public int BankAccount { get; set; }

        [CommandProperty(AccessLevel.GameMaster)]
        public int HoldGold { get; set; }

        [CommandProperty(AccessLevel.GameMaster)]
        public string ShopName
        {
            get => m_ShopName;
            set
            {
                m_ShopName = value ?? "";

                InvalidateProperties();
            }
        }

        [CommandProperty(AccessLevel.GameMaster)]
        public DateTime NextPayTime { get; private set; }

        public PlayerVendorPlaceholder Placeholder { get; set; }

        public BaseHouse House
        {
            get => m_House;
            set
            {
                m_House?.PlayerVendors.Remove(this);

                value?.PlayerVendors.Add(this);

                m_House = value;
            }
        }

        public int ChargePerDay
        {
            get
            {
                if (BaseHouse.NewVendorSystem)
                {
                    return ChargePerRealWorldDay / 12;
                }

                var total = m_SellItems.Values.Aggregate<VendorItem, long>(
                    0,
                    (current, vi) =>
                        current + vi.Price
                ) - 500;

                return (int)(20 + Math.Max(total, 0) / 500);
            }
        }

        public int ChargePerRealWorldDay
        {
            get
            {
                if (BaseHouse.NewVendorSystem)
                {
                    var total = m_SellItems.Values.Aggregate<VendorItem, long>(0, (current, vi) => current + vi.Price);

                    return (int)(60 + total / 500 * 3);
                }

                return ChargePerDay * 12;
            }
        }

        public override void Serialize(IGenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write(2); // version

            writer.Write(BaseHouse.NewVendorSystem);
            writer.Write(m_ShopName);
            writer.WriteDeltaTime(NextPayTime);
            writer.Write(House);

            writer.Write(Owner);
            writer.Write(BankAccount);
            writer.Write(HoldGold);

            writer.Write(m_SellItems.Count);
            foreach (var vi in m_SellItems.Values)
            {
                writer.Write(vi.Item);
                writer.Write(vi.Price);
                writer.Write(vi.Description);

                writer.Write(vi.Created);
            }
        }

        public override void Deserialize(IGenericReader reader)
        {
            base.Deserialize(reader);

            var version = reader.ReadInt();

            var newVendorSystem = false;

            switch (version)
            {
                case 2:
                case 1:
                    {
                        newVendorSystem = reader.ReadBool();
                        m_ShopName = reader.ReadString();
                        NextPayTime = reader.ReadDeltaTime();
                        House = (BaseHouse)reader.ReadEntity<Item>();

                        goto case 0;
                    }
                case 0:
                    {
                        Owner = reader.ReadEntity<Mobile>();
                        BankAccount = reader.ReadInt();
                        HoldGold = reader.ReadInt();

                        var count = reader.ReadInt();

                        m_SellItems = new Dictionary<Item, VendorItem>(count);

                        for (var i = 0; i < count; i++)
                        {
                            var item = reader.ReadEntity<Item>();

                            var price = reader.ReadInt();
                            if (price > 100000000)
                            {
                                price = 100000000;
                            }

                            var description = reader.ReadString();

                            var created = version < 1 ? Core.Now : reader.ReadDateTime();

                            if (item != null)
                            {
                                SetVendorItem(item, version < 1 && price <= 0 ? -1 : price, description, created);
                            }
                        }

                        break;
                    }
            }

            var newVendorSystemActivated = BaseHouse.NewVendorSystem && !newVendorSystem;

            if (version < 1 || newVendorSystemActivated)
            {
                if (version < 1)
                {
                    m_ShopName = "Shop Not Yet Named";
                    Timer.StartTimer(() => UpgradeFromVersion0(newVendorSystemActivated));
                }
                else
                {
                    Timer.StartTimer(FixDresswear);
                }

                NextPayTime = Core.Now + PayTimer.GetInterval();

                if (newVendorSystemActivated)
                {
                    HoldGold += BankAccount;
                    BankAccount = 0;
                }
            }

            if (version < 2 && RawStr == 75 && RawDex == 75 && RawInt == 75)
            {
                InitStats(100, 100, 25);
            }

            var delay = NextPayTime - Core.Now;

            m_PayTimer = new PayTimer(this, delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
            m_PayTimer.Start();

            Blessed = false;

            if (Core.AOS && NameHue == 0x35)
            {
                NameHue = -1;
            }
        }

        private void UpgradeFromVersion0(bool newVendorSystem)
        {
            var toRemove = new List<Item>();

            foreach (var vi in m_SellItems.Values)
            {
                if (!CanBeVendorItem(vi.Item))
                {
                    toRemove.Add(vi.Item);
                }
                else
                {
                    vi.Description = Utility.FixHtml(vi.Description);
                }
            }

            foreach (var item in toRemove)
            {
                RemoveVendorItem(item);
            }

            House = BaseHouse.FindHouseAt(this);

            if (newVendorSystem)
            {
                ActivateNewVendorSystem();
            }
        }

        private void ActivateNewVendorSystem()
        {
            FixDresswear();

            if (House?.IsOwner(Owner) == false)
            {
                Destroy(false);
            }
        }

        public void InitBody()
        {
            Hue = Race.Human.RandomSkinHue();
            SpeechHue = 0x3B2;

            if (!Core.AOS)
            {
                NameHue = 0x35;
            }

            if (Female = Utility.RandomBool())
            {
                Body = 0x191;
                Name = NameList.RandomName("female");
            }
            else
            {
                Body = 0x190;
                Name = NameList.RandomName("male");
            }
        }

        public virtual void InitOutfit()
        {
            Item item = new FancyShirt(Utility.RandomNeutralHue());
            item.Layer = Layer.InnerTorso;
            AddItem(item);
            AddItem(new LongPants(Utility.RandomNeutralHue()));
            AddItem(new BodySash(Utility.RandomNeutralHue()));
            AddItem(new Boots(Utility.RandomNeutralHue()));
            AddItem(new Cloak(Utility.RandomNeutralHue()));

            Utility.AssignRandomHair(this);

            Container pack = new VendorBackpack();
            pack.Movable = false;
            AddItem(pack);
        }

        public virtual bool IsOwner(Mobile m)
        {
            if (m.AccessLevel >= AccessLevel.GameMaster)
            {
                return true;
            }

            if (BaseHouse.NewVendorSystem && House != null)
            {
                return House.IsOwner(m);
            }

            return m == Owner;
        }

        protected List<Item> GetItems()
        {
            var list = new List<Item>();

            foreach (var item in Items)
            {
                if (item.Movable && item != Backpack && item.Layer != Layer.Hair && item.Layer != Layer.FacialHair)
                {
                    list.Add(item);
                }
            }

            if (Backpack != null)
            {
                list.AddRange(Backpack.Items);
            }

            return list;
        }

        public virtual void Destroy(bool toBackpack)
        {
            Return();

            if (!BaseHouse.NewVendorSystem)
            {
                FixDresswear();
            }

            /* Possible cases regarding item return:
             *
             * 1. No item must be returned
             *       -> do nothing.
             * 2. ( toBackpack is false OR the vendor is in the internal map ) AND the vendor is associated with a AOS house
             *       -> put the items into the moving crate or a vendor inventory,
             *          depending on whether the vendor owner is also the house owner.
             * 3. ( toBackpack is true OR the vendor isn't associated with any AOS house ) AND the vendor isn't in the internal map
             *       -> put the items into a backpack.
             * 4. The vendor isn't associated with any house AND it's in the internal map
             *       -> do nothing (we can't do anything).
             */

            var list = GetItems();

            if (list.Count > 0 || HoldGold > 0) // No case 1
            {
                if ((!toBackpack || Map == Map.Internal) && House?.IsAosRules == true) // Case 2
                {
                    if (House.IsOwner(Owner)) // Move to moving crate
                    {
                        House.MovingCrate ??= new MovingCrate(House);

                        if (HoldGold > 0)
                        {
                            Banker.Deposit(House.MovingCrate, HoldGold);
                        }

                        foreach (var item in list)
                        {
                            House.MovingCrate.DropItem(item);
                        }
                    }
                    else // Move to vendor inventory
                    {
                        var inventory = new VendorInventory(House, Owner, Name, ShopName);
                        inventory.Gold = HoldGold;

                        foreach (var item in list)
                        {
                            inventory.AddItem(item);
                        }

                        House.VendorInventories.Add(inventory);
                    }
                }
                else if ((toBackpack || House?.IsAosRules != true) && Map != Map.Internal) // Case 3 - Move to backpack
                {
                    Container backpack = new Backpack();

                    if (HoldGold > 0)
                    {
                        Banker.Deposit(backpack, HoldGold);
                    }

                    foreach (var item in list)
                    {
                        backpack.DropItem(item);
                    }

                    backpack.MoveToWorld(Location, Map);
                }
            }

            Delete();
        }

        private void FixDresswear()
        {
            for (var i = 0; i < Items.Count; ++i)
            {
                var item = Items[i];

                item.Layer = item switch
                {
                    BaseHat         => Layer.Helm,
                    BaseMiddleTorso => Layer.MiddleTorso,
                    BaseOuterLegs   => Layer.OuterLegs,
                    BaseOuterTorso  => Layer.OuterTorso,
                    BasePants       => Layer.Pants,
                    BaseShirt       => Layer.Shirt,
                    BaseWaist       => Layer.Waist,
                    BaseShoes       => Layer.Shoes,
                    _               => item.Layer
                };

                if (item is Sandals)
                {
                    item.Hue = 0;
                }
            }
        }

        public override void OnAfterDelete()
        {
            base.OnAfterDelete();

            m_PayTimer.Stop();

            House = null;

            Placeholder?.Delete();
        }

        public override bool IsSnoop(Mobile from) => false;

        public override void GetProperties(IPropertyList list)
        {
            base.GetProperties(list);

            if (BaseHouse.NewVendorSystem)
            {
                list.Add(1062449, ShopName); // Shop Name: ~1_NAME~
            }
        }

        public VendorItem GetVendorItem(Item item)
        {
            m_SellItems.TryGetValue(item, out var v);
            return v;
        }

        private VendorItem SetVendorItem(Item item, int price, string description) =>
            SetVendorItem(item, price, description, Core.Now);

        private VendorItem SetVendorItem(Item item, int price, string description, DateTime created)
        {
            RemoveVendorItem(item);

            var vi = new VendorItem(item, price, description, created);
            m_SellItems[item] = vi;

            item.InvalidateProperties();

            return vi;
        }

        private void RemoveVendorItem(Item item)
        {
            var vi = GetVendorItem(item);

            if (vi != null)
            {
                vi.Invalidate();
                m_SellItems.Remove(item);

                foreach (var subItem in item.Items)
                {
                    RemoveVendorItem(subItem);
                }

                item.InvalidateProperties();
            }
        }

        private bool CanBeVendorItem(Item item)
        {
            var parent = item.Parent as Item;

            if (parent == Backpack)
            {
                return true;
            }

            if (parent is Container)
            {
                var parentVI = GetVendorItem(parent);

                if (parentVI != null)
                {
                    return !parentVI.IsForSale;
                }
            }

            return false;
        }

        public override void OnSubItemAdded(Item item)
        {
            base.OnSubItemAdded(item);

            if (GetVendorItem(item) == null && CanBeVendorItem(item))
            {
                SetVendorItem(item, 999, "");
            }
        }

        public override void OnSubItemRemoved(Item item)
        {
            base.OnSubItemRemoved(item);

            if (item.GetBounce() == null)
            {
                RemoveVendorItem(item);
            }
        }

        public override void OnSubItemBounceCleared(Item item)
        {
            base.OnSubItemBounceCleared(item);

            if (!CanBeVendorItem(item))
            {
                RemoveVendorItem(item);
            }
        }

        public override void OnItemRemoved(Item item)
        {
            base.OnItemRemoved(item);

            if (item == Backpack)
            {
                foreach (var subItem in item.Items)
                {
                    RemoveVendorItem(subItem);
                }
            }
        }

        public override bool OnDragDrop(Mobile from, Item item)
        {
            if (!IsOwner(from))
            {
                SayTo(from, 503209); // I can only take item from the shop owner.
                return false;
            }

            if (item is Gold)
            {
                if (BaseHouse.NewVendorSystem)
                {
                    if (HoldGold < 1000000)
                    {
                        SayTo(from, 503210); // I'll take that to fund my services.

                        HoldGold += item.Amount;
                        item.Delete();

                        return true;
                    }

                    from.SendLocalizedMessage(
                        1062493
                    ); // Your vendor has sufficient funds for operation and cannot accept this gold.

                    return false;
                }

                if (BankAccount < 1000000)
                {
                    SayTo(from, 503210); // I'll take that to fund my services.

                    BankAccount += item.Amount;
                    item.Delete();

                    return true;
                }

                from.SendLocalizedMessage(
                    1062493
                ); // Your vendor has sufficient funds for operation and cannot accept this gold.

                return false;
            }

            var newItem = GetVendorItem(item) == null;

            if (Backpack?.TryDropItem(from, item, false) == true)
            {
                if (newItem)
                {
                    OnItemGiven(from, item);
                }

                return true;
            }

            SayTo(from, 503211); // I can't carry any more.
            return false;
        }

        public override bool CheckNonlocalDrop(Mobile from, Item item, Item target)
        {
            if (IsOwner(from))
            {
                if (GetVendorItem(item) == null)
                {
                    Timer.StartTimer(() => OnItemGiven(from, item));
                }

                return true;
            }

            SayTo(from, 503209); // I can only take item from the shop owner.
            return false;
        }

        private void OnItemGiven(Mobile from, Item item)
        {
            var vi = GetVendorItem(item);

            if (vi == null)
            {
                return;
            }

            var name = item.Name.DefaultIfNullOrEmpty($"#{item.LabelNumber}");

            from.SendLocalizedMessage(1043303, name); // Type in a price and description for ~1_ITEM~ (ESC=not for sale)
            from.Prompt = new VendorPricePrompt(this, vi);
        }

        public override bool AllowEquipFrom(Mobile from) =>
            BaseHouse.NewVendorSystem && IsOwner(from) || base.AllowEquipFrom(from);

        public override bool CheckNonlocalLift(Mobile from, Item item)
        {
            if (item.IsChildOf(Backpack))
            {
                if (IsOwner(from))
                {
                    return true;
                }

                SayTo(from, 503223); // If you'd like to purchase an item, just ask.
                return false;
            }

            if (BaseHouse.NewVendorSystem && IsOwner(from))
            {
                return true;
            }

            return base.CheckNonlocalLift(from, item);
        }

        public bool CanInteractWith(Mobile from, bool ownerOnly)
        {
            if (!from.CanSee(this) || !Utility.InUpdateRange(from.Location, Location) || !from.CheckAlive())
            {
                return false;
            }

            if (ownerOnly)
            {
                return IsOwner(from);
            }

            if (House?.IsBanned(from) == true && !IsOwner(from))
            {
                from.SendLocalizedMessage(
                    1062674
                ); // You can't shop from this home as you have been banned from this establishment.
                return false;
            }

            return true;
        }

        public override void OnDoubleClick(Mobile from)
        {
            if (IsOwner(from))
            {
                SendOwnerGump(from);
            }
            else if (CanInteractWith(from, false))
            {
                OpenBackpack(from);
            }
        }

        public override void DisplayPaperdollTo(Mobile m)
        {
            if (BaseHouse.NewVendorSystem)
            {
                base.DisplayPaperdollTo(m);
            }
            else if (CanInteractWith(m, false))
            {
                OpenBackpack(m);
            }
        }

        public void SendOwnerGump(Mobile to)
        {
            if (BaseHouse.NewVendorSystem)
            {
                to.CloseGump<NewPlayerVendorOwnerGump>();
                to.CloseGump<NewPlayerVendorCustomizeGump>();

                to.SendGump(new NewPlayerVendorOwnerGump(this));
            }
            else
            {
                to.CloseGump<PlayerVendorOwnerGump>();
                to.CloseGump<PlayerVendorCustomizeGump>();

                to.SendGump(new PlayerVendorOwnerGump(this));
            }
        }

        public void OpenBackpack(Mobile from)
        {
            if (Backpack != null)
            {
                SayTo(from, IsOwner(from) ? 1010642 : 503208); // Take a look at my/your goods.

                Backpack.DisplayTo(from);
            }
        }

        public static void TryToBuy(Item item, Mobile from)
        {
            if (item.RootParent is not PlayerVendor vendor || !vendor.CanInteractWith(from, false))
            {
                return;
            }

            if (vendor.IsOwner(from))
            {
                vendor.SayTo(from, 503212); // You own this shop, just take what you want.
                return;
            }

            var vi = vendor.GetVendorItem(item);

            if (vi == null)
            {
                vendor.SayTo(from, 503216); // You can't buy that.
            }
            else if (!vi.IsForSale)
            {
                vendor.SayTo(from, 503202); // This item is not for sale.
            }
            else if (vi.Created + TimeSpan.FromMinutes(1.0) > Core.Now)
            {
                from.SendMessage("You cannot buy this item right now.  Please wait one minute and try again.");
            }
            else
            {
                from.CloseGump<PlayerVendorBuyGump>();
                from.SendGump(new PlayerVendorBuyGump(vendor, vi));
            }
        }

        public void CollectGold(Mobile to)
        {
            if (HoldGold > 0)
            {
                SayTo(to, $"How much of the {HoldGold} that I'm holding would you like?");
                to.SendMessage("Enter the amount of gold you wish to withdraw (ESC = CANCEL):");

                to.Prompt = new CollectGoldPrompt(this);
            }
            else
            {
                SayTo(to, 503215); // I am holding no gold for you.
            }
        }

        public int GiveGold(Mobile to, int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }

            if (amount > HoldGold)
            {
                SayTo(to, $"I'm sorry, but I'm only holding {HoldGold} gold for you.");
                return 0;
            }

            var amountGiven = Banker.DepositUpTo(to, amount);
            HoldGold -= amountGiven;

            if (amountGiven > 0)
            {
                to.SendLocalizedMessage(
                    1060397,
                    amountGiven.ToString()
                ); // ~1_AMOUNT~ gold has been deposited into your bank box.
            }

            if (amountGiven == 0)
            {
                SayTo(
                    to,
                    1070755
                ); // Your bank box cannot hold the gold you are requesting.  I will keep the gold until you can take it.
            }
            else if (amount > amountGiven)
            {
                SayTo(
                    to,
                    1070756
                ); // I can only give you part of the gold now, as your bank box is too full to hold the full amount.
            }
            else if (HoldGold > 0)
            {
                SayTo(to, 1042639); // Your gold has been transferred.
            }
            else
            {
                SayTo(to, 503234); // All the gold I have been carrying for you has been deposited into your bank account.
            }

            return amountGiven;
        }

        public void Dismiss(Mobile from)
        {
            var pack = Backpack;

            if (pack?.Items.Count > 0)
            {
                SayTo(from, 1038325); // You cannot dismiss me while I am holding your goods.
                return;
            }

            if (HoldGold > 0)
            {
                GiveGold(from, HoldGold);

                if (HoldGold > 0)
                {
                    return;
                }
            }

            Destroy(true);
        }

        public void Rename(Mobile from)
        {
            from.SendLocalizedMessage(1062494); // Enter a new name for your vendor (20 characters max):

            from.Prompt = new VendorNamePrompt(this);
        }

        public void RenameShop(Mobile from)
        {
            from.SendLocalizedMessage(1062433); // Enter a new name for your shop (20 chars max):

            from.Prompt = new ShopNamePrompt(this);
        }

        public bool CheckTeleport(Mobile to)
        {
            if (Deleted || !IsOwner(to) || House == null || Map == Map.Internal)
            {
                return false;
            }

            if (House.IsInside(to) || to.Map != House.Map || !House.InRange(to.Location, 5))
            {
                return false;
            }

            if (Placeholder == null)
            {
                Placeholder = new PlayerVendorPlaceholder(this);
                Placeholder.MoveToWorld(Location, Map);

                MoveToWorld(to.Location, to.Map);

                to.SendLocalizedMessage(
                    1062431
                ); // This vendor has been moved out of the house to your current location temporarily.  The vendor will return home automatically after two minutes have passed once you are done managing its inventory or customizing it.
            }
            else
            {
                Placeholder.RestartTimer();

                to.SendLocalizedMessage(
                    1062430
                ); // This vendor is currently temporarily in a location outside its house.  The vendor will return home automatically after two minutes have passed once you are done managing its inventory or customizing it.
            }

            return true;
        }

        public void Return()
        {
            Placeholder?.Delete();
        }

        public override void GetContextMenuEntries(Mobile from, List<ContextMenuEntry> list)
        {
            if (from.Alive && Placeholder != null && IsOwner(from))
            {
                list.Add(new ReturnVendorEntry(this));
            }

            base.GetContextMenuEntries(from, list);
        }

        public override bool HandlesOnSpeech(Mobile from) => from.Alive && from.GetDistanceToSqrt(this) <= 3;

        public bool WasNamed(string speech) => Name != null && speech.InsensitiveStartsWith(Name);

        public override void OnSpeech(SpeechEventArgs e)
        {
            var from = e.Mobile;

            if (e.Handled || !from.Alive || from.GetDistanceToSqrt(this) > 3)
            {
                return;
            }

            if (e.HasKeyword(0x3C) || e.HasKeyword(0x171) && WasNamed(e.Speech)) // vendor buy, *buy*
            {
                if (IsOwner(from))
                {
                    SayTo(from, 503212); // You own this shop, just take what you want.
                }
                else if (House?.IsBanned(from) != true)
                {
                    from.SendLocalizedMessage(503213); // Select the item you wish to buy.
                    from.Target = new PVBuyTarget();

                    e.Handled = true;
                }
            }
            else if (e.HasKeyword(0x3D) || e.HasKeyword(0x172) && WasNamed(e.Speech)) // vendor browse, *browse
            {
                if (House?.IsBanned(from) == true && !IsOwner(from))
                {
                    SayTo(from, 1062674); // You can't shop from this home as you have been banned from this establishment.
                }
                else
                {
                    if (WasNamed(e.Speech))
                    {
                        OpenBackpack(from);
                    }
                    else
                    {
                        var mobiles = e.Mobile.GetMobilesInRange<PlayerVendor>(2);

                        foreach (var m in mobiles)
                        {
                            if (m.CanSee(e.Mobile) && m.InLOS(e.Mobile))
                            {
                                m.OpenBackpack(from);
                            }
                        }
                    }

                    e.Handled = true;
                }
            }
            else if (e.HasKeyword(0x3E) || e.HasKeyword(0x173) && WasNamed(e.Speech)) // vendor collect, *collect
            {
                if (IsOwner(from))
                {
                    CollectGold(from);

                    e.Handled = true;
                }
            }
            else if (e.HasKeyword(0x3F) || e.HasKeyword(0x174) && WasNamed(e.Speech)) // vendor status, *status
            {
                if (IsOwner(from))
                {
                    SendOwnerGump(from);

                    e.Handled = true;
                }
                else
                {
                    SayTo(from, 503226); // What do you care? You don't run this shop.
                }
            }
            else if (e.HasKeyword(0x40) || e.HasKeyword(0x175) && WasNamed(e.Speech)) // vendor dismiss, *dismiss
            {
                if (IsOwner(from))
                {
                    Dismiss(from);

                    e.Handled = true;
                }
            }
            else if (e.HasKeyword(0x41) || e.HasKeyword(0x176) && WasNamed(e.Speech)) // vendor cycle, *cycle
            {
                if (IsOwner(from))
                {
                    Direction = GetDirectionTo(from);

                    e.Handled = true;
                }
            }
        }

        public override bool CanBeDamaged() => false;

        private class ReturnVendorEntry : ContextMenuEntry
        {
            private readonly PlayerVendor m_Vendor;

            public ReturnVendorEntry(PlayerVendor vendor) : base(6214) => m_Vendor = vendor;

            public override void OnClick()
            {
                var from = Owner.From;

                if (!m_Vendor.Deleted && m_Vendor.IsOwner(from) && from.CheckAlive())
                {
                    m_Vendor.Return();
                }
            }
        }

        private class PayTimer : Timer
        {
            private readonly PlayerVendor m_Vendor;

            public PayTimer(PlayerVendor vendor, TimeSpan delay) : base(delay, GetInterval())
            {
                m_Vendor = vendor;
            }

            public static TimeSpan GetInterval()
            {
                if (BaseHouse.NewVendorSystem)
                {
                    return TimeSpan.FromDays(1.0);
                }

                return TimeSpan.FromMinutes(Clock.MinutesPerUODay);
            }

            protected override void OnTick()
            {
                m_Vendor.NextPayTime = Core.Now + Interval;

                int pay;
                int totalGold;
                if (BaseHouse.NewVendorSystem)
                {
                    pay = m_Vendor.ChargePerRealWorldDay;
                    totalGold = m_Vendor.HoldGold;
                }
                else
                {
                    pay = m_Vendor.ChargePerDay;
                    totalGold = m_Vendor.BankAccount + m_Vendor.HoldGold;
                }

                if (pay > totalGold)
                {
                    m_Vendor.Destroy(!BaseHouse.NewVendorSystem);
                }
                else
                {
                    if (!BaseHouse.NewVendorSystem)
                    {
                        if (m_Vendor.BankAccount >= pay)
                        {
                            m_Vendor.BankAccount -= pay;
                            pay = 0;
                        }
                        else
                        {
                            pay -= m_Vendor.BankAccount;
                            m_Vendor.BankAccount = 0;
                        }
                    }

                    m_Vendor.HoldGold -= pay;
                }
            }
        }

        [PlayerVendorTarget]
        private class PVBuyTarget : Target
        {
            public PVBuyTarget() : base(3, false, TargetFlags.None) => AllowNonlocal = true;

            protected override void OnTarget(Mobile from, object targeted)
            {
                if (targeted is Item item)
                {
                    TryToBuy(item, from);
                }
            }
        }

        private class VendorPricePrompt : Prompt
        {
            private readonly PlayerVendor m_Vendor;
            private readonly VendorItem m_VI;

            public VendorPricePrompt(PlayerVendor vendor, VendorItem vi)
            {
                m_Vendor = vendor;
                m_VI = vi;
            }

            public override void OnResponse(Mobile from, string text)
            {
                if (!m_VI.Valid || !m_Vendor.CanInteractWith(from, true))
                {
                    return;
                }

                string firstWord;

                var sep = text.IndexOfAny(new[] { ' ', ',' });
                firstWord = sep >= 0 ? text[..sep] : text;

                string description;

                if (int.TryParse(firstWord, out var price))
                {
                    description = sep >= 0 ? text[(sep + 1)..].Trim() : "";
                }
                else
                {
                    price = -1;
                    description = text.Trim();
                }

                SetInfo(from, price, Utility.FixHtml(description));
            }

            public override void OnCancel(Mobile from)
            {
                if (!m_VI.Valid || !m_Vendor.CanInteractWith(from, true))
                {
                    return;
                }

                SetInfo(from, -1, "");
            }

            private void SetInfo(Mobile from, int price, string description)
            {
                var item = m_VI.Item;

                var setPrice = false;

                if (price < 0) // Not for sale
                {
                    price = -1;

                    if (item is Container)
                    {
                        if (item is LockableContainer container && container.Locked)
                        {
                            m_Vendor.SayTo(from, 1043298); // Locked items may not be made not-for-sale.
                        }
                        else if (item.Items.Count > 0)
                        {
                            m_Vendor.SayTo(from, 1043299); // To be not for sale, all items in a container must be for sale.
                        }
                        else
                        {
                            setPrice = true;
                        }
                    }
                    else if (item is BaseBook or BulkOrderBook)
                    {
                        setPrice = true;
                    }
                    else
                    {
                        m_Vendor.SayTo(
                            from,
                            1043301
                        ); // Only the following may be made not-for-sale: books, containers, keyrings, and items in for-sale containers.
                    }
                }
                else
                {
                    if (price > 100000000)
                    {
                        price = 100000000;
                        from.SendMessage("You cannot price items above 100,000,000 gold.  The price has been adjusted.");
                    }

                    setPrice = true;
                }

                if (setPrice)
                {
                    m_Vendor.SetVendorItem(item, price, description);
                }
                else
                {
                    m_VI.Description = description;
                }
            }
        }

        private class CollectGoldPrompt : Prompt
        {
            private readonly PlayerVendor m_Vendor;

            public CollectGoldPrompt(PlayerVendor vendor) => m_Vendor = vendor;

            public override void OnResponse(Mobile from, string text)
            {
                if (!m_Vendor.CanInteractWith(from, true))
                {
                    return;
                }

                text = text.Trim();

                if (!int.TryParse(text, out var amount))
                {
                    amount = 0;
                }

                GiveGold(from, amount);
            }

            public override void OnCancel(Mobile from)
            {
                if (!m_Vendor.CanInteractWith(from, true))
                {
                    return;
                }

                GiveGold(from, 0);
            }

            private void GiveGold(Mobile to, int amount)
            {
                if (amount <= 0)
                {
                    m_Vendor.SayTo(to, "Very well. I will hold on to the money for now then.");
                }
                else
                {
                    m_Vendor.GiveGold(to, amount);
                }
            }
        }

        private class VendorNamePrompt : Prompt
        {
            private readonly PlayerVendor m_Vendor;

            public VendorNamePrompt(PlayerVendor vendor) => m_Vendor = vendor;

            public override void OnResponse(Mobile from, string text)
            {
                if (!m_Vendor.CanInteractWith(from, true))
                {
                    return;
                }

                var name = text.Trim();

                if (!NameVerification.Validate(name, 1, 20, true, true, true, 0, NameVerification.Empty))
                {
                    m_Vendor.SayTo(from, "That name is unacceptable.");
                    return;
                }

                m_Vendor.Name = Utility.FixHtml(name);

                from.SendLocalizedMessage(1062496); // Your vendor has been renamed.

                from.SendGump(new NewPlayerVendorOwnerGump(m_Vendor));
            }
        }

        private class ShopNamePrompt : Prompt
        {
            private readonly PlayerVendor m_Vendor;

            public ShopNamePrompt(PlayerVendor vendor) => m_Vendor = vendor;

            public override void OnResponse(Mobile from, string text)
            {
                if (!m_Vendor.CanInteractWith(from, true))
                {
                    return;
                }

                var name = text.Trim();

                if (!NameVerification.Validate(name, 1, 20, true, true, true, 0, NameVerification.Empty))
                {
                    m_Vendor.SayTo(from, "That name is unacceptable.");
                    return;
                }

                m_Vendor.ShopName = Utility.FixHtml(name);

                from.SendGump(new NewPlayerVendorOwnerGump(m_Vendor));
            }
        }
    }

    public class PlayerVendorPlaceholder : Item
    {
        private readonly ExpireTimer m_Timer;

        public PlayerVendorPlaceholder(PlayerVendor vendor) : base(0x1F28)
        {
            Hue = 0x672;
            Movable = false;

            Vendor = vendor;

            m_Timer = new ExpireTimer(this);
            m_Timer.Start();
        }

        public PlayerVendorPlaceholder(Serial serial) : base(serial)
        {
        }

        [CommandProperty(AccessLevel.GameMaster)]
        public PlayerVendor Vendor { get; private set; }

        public override void GetProperties(IPropertyList list)
        {
            base.GetProperties(list);

            if (Vendor != null)
            {
                list.Add(1062498, Vendor.Name); // reserved for vendor ~1_NAME~
            }
        }

        public void RestartTimer()
        {
            m_Timer.Stop();
            m_Timer.Start();
        }

        public override void OnDelete()
        {
            if (Vendor?.Deleted == false)
            {
                Vendor.MoveToWorld(Location, Map);
                Vendor.Placeholder = null;
            }
        }

        public override void Serialize(IGenericWriter writer)
        {
            base.Serialize(writer);

            writer.WriteEncodedInt(0);

            writer.Write(Vendor);
        }

        public override void Deserialize(IGenericReader reader)
        {
            base.Deserialize(reader);

            var version = reader.ReadEncodedInt();

            Vendor = (PlayerVendor)reader.ReadEntity<Mobile>();

            Timer.StartTimer(Delete);
        }

        private class ExpireTimer : Timer
        {
            private readonly PlayerVendorPlaceholder m_Placeholder;

            public ExpireTimer(PlayerVendorPlaceholder placeholder) : base(TimeSpan.FromMinutes(2.0))
            {
                m_Placeholder = placeholder;
            }

            protected override void OnTick()
            {
                m_Placeholder.Delete();
            }
        }
    }
}
