using System.Globalization;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShoppyShop.Infrastructure.Migrations
{
    /// <summary>
    /// Adds six products to every category (ids {group}-10 to {group}-15), taking each from nine
    /// products to fifteen. catalogue.json carries the same rows, so a database upgraded in place
    /// ends up identical to one seeded fresh from that file. The seed only runs against an empty
    /// catalogue, so without this migration an existing database would never receive them.
    /// </summary>
    public partial class AddCatalogueProducts : Migration
    {
        private const string Columns =
            "\"Id\", \"GroupId\", \"Name\", \"Brand\", \"Description\", \"ImageUrl\", \"Price\", \"SalePrice\", \"InStock\", \"IsNew\", \"GiftWrappable\"";

        private static readonly NewProduct[] Products =
        [
            new("beauty-10", "beauty", "Vitamin C Brightening Serum", "Sollen Skin",
                "A lightweight 15% vitamin C serum with ferulic acid that brightens dull skin and evens tone, in an amber dropper bottle.",
                "https://images.unsplash.com/photo-1713768704571-6aeb0d0e5105?auto=format&fit=crop&w=800&h=600&q=80",
                58.00m, 46.40m, InStock: true, IsNew: true, GiftWrappable: true),
            new("beauty-11", "beauty", "Kaolin Clay Purifying Mask", "Fernmoor Botanics",
                "A creamy white-clay mask with green tea and willow bark that lifts impurities without leaving skin tight.",
                "https://images.unsplash.com/photo-1779437141537-62577b216307?auto=format&fit=crop&w=800&h=600&q=80",
                36.50m, null, InStock: true, IsNew: true, GiftWrappable: false),
            new("beauty-12", "beauty", "Warm Nudes Eyeshadow Palette", "Loma Studio",
                "Blendable matte and shimmer shades from soft champagne to deep bronze, for everything from a daytime wash to a smoky evening eye.",
                "https://images.unsplash.com/photo-1583241801142-113b9f5bbde5?auto=format&fit=crop&w=800&h=600&q=80",
                48.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("beauty-13", "beauty", "Whipped Rose Body Butter", "Fernmoor Botanics",
                "Shea and cocoa butters whipped with rose geranium into a rich, fast-absorbing cream for dry skin.",
                "https://images.unsplash.com/photo-1765964492963-b0aa8c172431?auto=format&fit=crop&w=800&h=600&q=80",
                32.00m, 25.60m, InStock: true, IsNew: false, GiftWrappable: true),
            new("beauty-14", "beauty", "Handmade Botanical Soap Bars, Set of 4", "Fernmoor Botanics",
                "Cold-process olive oil soaps in lavender, oatmeal, honey and charcoal, cut by hand and tied with jute twine.",
                "https://images.unsplash.com/photo-1600857544200-b2f666a9a2ec?auto=format&fit=crop&w=800&h=600&q=80",
                24.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("beauty-15", "beauty", "Rose Petal Mineral Bath Soak", "Sollen Skin",
                "Dead Sea and Epsom salts with dried rose petals and a drop of geranium oil, in a reusable glass jar.",
                "https://images.unsplash.com/photo-1550623685-2227f7bbef18?auto=format&fit=crop&w=800&h=600&q=80",
                29.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("electronics-10", "electronics", "Broadcast Microphone with Boom Arm", "Solace Audio",
                "A dynamic broadcast microphone with an internal shock mount, supplied on a spring-balanced desk arm for podcasts and streaming.",
                "https://images.unsplash.com/photo-1590602847861-f357a9332bbc?auto=format&fit=crop&w=800&h=600&q=80",
                219.00m, 175.20m, InStock: true, IsNew: true, GiftWrappable: true),
            new("electronics-11", "electronics", "Silent Wireless Mouse", "Keystead",
                "A slim, low-profile mouse with near-silent clicks, a rechargeable battery and both Bluetooth and USB-receiver pairing.",
                "https://images.unsplash.com/photo-1707592691247-5c3a1c7ba0e3?auto=format&fit=crop&w=800&h=600&q=80",
                34.90m, null, InStock: true, IsNew: true, GiftWrappable: false),
            new("electronics-12", "electronics", "Full HD Home Projector", "Northwave",
                "A ceiling-mountable 1080p LED projector with keystone correction and a picture of up to 150 inches for movie nights.",
                "https://images.unsplash.com/photo-1728771175581-3cb1e21fac92?auto=format&fit=crop&w=800&h=600&q=80",
                349.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("electronics-13", "electronics", "Retro Portable Radio", "Northwave",
                "An FM and Bluetooth radio with a carry handle, a rotary tuning dial and a 20-hour rechargeable battery.",
                "https://images.unsplash.com/photo-1703669059784-b40068f30c0c?auto=format&fit=crop&w=800&h=600&q=80",
                79.00m, 63.20m, InStock: true, IsNew: false, GiftWrappable: true),
            new("electronics-14", "electronics", "Wireless Charging Pad", "Keystead",
                "A slim 15 W Qi charging pad with a soft-touch top and anti-slip base that charges phones and earbuds through most cases.",
                "https://images.unsplash.com/photo-1591290619618-904f6dd935e3?auto=format&fit=crop&w=800&h=600&q=80",
                29.95m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("electronics-15", "electronics", "Flip Desk Clock", "Northwave",
                "A battery-powered flip clock with crisp black-on-white number cards and a near-silent movement, on a solid pine base.",
                "https://images.unsplash.com/photo-1749703996043-f9a937fe6d52?auto=format&fit=crop&w=800&h=600&q=80",
                64.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("fashion-10", "fashion", "Hand-Knit Leaf Stitch Jumper", "Wren",
                "A chunky hand-knitted jumper in soft teal wool, with a leaf-stitch panel down the front and a relaxed dropped shoulder.",
                "https://images.unsplash.com/photo-1536992266094-82847e1fd431?auto=format&fit=crop&w=800&h=600&q=80",
                168.00m, 134.40m, InStock: true, IsNew: true, GiftWrappable: true),
            new("fashion-11", "fashion", "Straight-Leg Selvedge Jeans", "Fieldhouse",
                "Mid-wash straight-leg jeans in 13 oz selvedge denim that softens and fades with every wear.",
                "https://images.unsplash.com/photo-1714143136372-ddaf8b606da7?auto=format&fit=crop&w=800&h=600&q=80",
                129.00m, null, InStock: true, IsNew: true, GiftWrappable: false),
            new("fashion-12", "fashion", "Relaxed Linen Shirt", "Fieldhouse",
                "A breathable shirt in washed navy linen with a soft collar and a relaxed fit that works tucked in or worn loose.",
                "https://images.unsplash.com/photo-1740711152088-88a009e877bb?auto=format&fit=crop&w=800&h=600&q=80",
                89.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("fashion-13", "fashion", "Belted Cotton Trench Coat", "Atelier Norde",
                "A water-repellent cotton-gabardine trench in black, with a storm flap, buckled belt and cuff straps.",
                "https://images.unsplash.com/photo-1724473008282-c3a90be39f6b?auto=format&fit=crop&w=800&h=600&q=80",
                329.00m, 263.20m, InStock: true, IsNew: false, GiftWrappable: true),
            new("fashion-14", "fashion", "Leather Chelsea Boots", "Cobble & Last",
                "Pull-on Chelsea boots in burnished brown leather, with elastic side panels and a stitched leather sole.",
                "https://images.unsplash.com/photo-1777987601423-f350ac29b3e9?auto=format&fit=crop&w=800&h=600&q=80",
                239.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("fashion-15", "fashion", "Organic Cotton Crew T-Shirt", "Fieldhouse",
                "An everyday crew-neck T-shirt in mid-weight organic cotton jersey, cut for a clean, straight fit.",
                "https://images.unsplash.com/photo-1778671394516-8270eac13c42?auto=format&fit=crop&w=800&h=600&q=80",
                32.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("home-10", "home", "Chunky Knit Throw", "Loomward",
                "An oversized hand-knitted throw in cream chenille, heavy enough to feel cosy draped over a sofa or the end of a bed.",
                "https://images.unsplash.com/photo-1674475762498-75310193b4f4?auto=format&fit=crop&w=800&h=600&q=80",
                129.00m, 103.20m, InStock: true, IsNew: true, GiftWrappable: true),
            new("home-11", "home", "Stonewashed Linen Bedding Set", "Loomward",
                "A duvet cover, fitted sheet and pillowcases in stonewashed French linen, in white and sage, soft from the very first night.",
                "https://images.unsplash.com/photo-1639813806535-b206d3dcf3b6?auto=format&fit=crop&w=800&h=600&q=80",
                219.00m, null, InStock: true, IsNew: true, GiftWrappable: false),
            new("home-12", "home", "Round Oak Wall Mirror", "Oak & Ember",
                "A 70 cm round mirror framed in solid oiled oak, sized for a hallway or above a console table.",
                "https://images.unsplash.com/photo-1618220252344-8ec99ec624b1?auto=format&fit=crop&w=800&h=600&q=80",
                189.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("home-13", "home", "Stoneware Table Lamp", "Brightwork",
                "A rounded charcoal stoneware base under a tapered amber shade that casts a warm, diffused glow.",
                "https://images.unsplash.com/photo-1573676386604-78f8ed228e2b?auto=format&fit=crop&w=800&h=600&q=80",
                145.00m, 116.00m, InStock: true, IsNew: false, GiftWrappable: true),
            new("home-14", "home", "Terracotta Planter", "Kiln Street",
                "A hand-thrown terracotta pot with a limewash-streaked finish and a drainage hole, for succulents and small houseplants.",
                "https://images.unsplash.com/photo-1616856762053-425b01f896cc?auto=format&fit=crop&w=800&h=600&q=80",
                28.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("home-15", "home", "Nesting Palm-Leaf Baskets", "Loomward",
                "A nesting set of baskets hand-woven from natural palm leaf, for fruit, keys or everyday odds and ends.",
                "https://images.unsplash.com/photo-1626037235530-fe56de7d6459?auto=format&fit=crop&w=800&h=600&q=80",
                46.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("accessories-10", "accessories", "Browline Optical Frames", "Ostra Eyewear",
                "Classic browline frames with a tortoiseshell acetate brow and slim gold-tone rims, ready for prescription lenses.",
                "https://images.unsplash.com/photo-1574258495973-f010dfbb5371?auto=format&fit=crop&w=800&h=600&q=80",
                129.00m, 103.20m, InStock: true, IsNew: true, GiftWrappable: true),
            new("accessories-11", "accessories", "Gold Huggie Hoop Earrings", "Maren Jewellery",
                "Small, chunky 18k gold-plated hoops that hug the lobe, with a hinged clasp and light enough to wear every day.",
                "https://images.unsplash.com/photo-1632525230528-ec17c49bc168?auto=format&fit=crop&w=800&h=600&q=80",
                68.00m, null, InStock: true, IsNew: true, GiftWrappable: false),
            new("accessories-12", "accessories", "Braided Leather Belt", "Tanner & Vale",
                "A hand-braided belt in chestnut leather with a brushed silver buckle; the weave lets you fasten it at any length.",
                "https://images.unsplash.com/photo-1711443982852-b3df5c563448?auto=format&fit=crop&w=800&h=600&q=80",
                79.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("accessories-13", "accessories", "Straw Fedora", "Brimwell",
                "A woven-straw fedora with a striped grosgrain band, for sunny days in town or on the coast.",
                "https://images.unsplash.com/photo-1725398807874-948f9721aad9?auto=format&fit=crop&w=800&h=600&q=80",
                58.00m, 46.40m, InStock: true, IsNew: false, GiftWrappable: true),
            new("accessories-14", "accessories", "Striped Fringed Scarf", "Loomward",
                "A soft wool-blend scarf woven in stripes of berry, rose and slate, finished with a knotted fringe.",
                "https://images.unsplash.com/photo-1609803384069-19f3e5a70e75?auto=format&fit=crop&w=800&h=600&q=80",
                54.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("accessories-15", "accessories", "Woven Market Tote", "Tanner & Vale",
                "A roomy woven tote with riveted leather handles, sturdy enough for the market and easy to wipe clean.",
                "https://images.unsplash.com/photo-1524679813234-66a389fe1a42?auto=format&fit=crop&w=800&h=600&q=80",
                89.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("gifts-10", "gifts", "Glass Succulent Terrarium", "Little Grove",
                "A hand-blown glass globe planted with living succulents, moss and stones, needing only a little water every few weeks.",
                "https://images.unsplash.com/photo-1416339411116-62e1226aacd8?auto=format&fit=crop&w=800&h=600&q=80",
                64.00m, 51.20m, InStock: true, IsNew: true, GiftWrappable: true),
            new("gifts-11", "gifts", "Dried Flower Bouquet", "Little Grove",
                "Everlasting flowers and grasses in soft blush tones, wrapped in paper and tied with ribbon, made to last for months.",
                "https://images.unsplash.com/photo-1622658641558-235f26dd270b?auto=format&fit=crop&w=800&h=600&q=80",
                42.00m, null, InStock: true, IsNew: true, GiftWrappable: false),
            new("gifts-12", "gifts", "Old Town Jigsaw Puzzle, 1000 Pieces", "Rook & Rank",
                "A finely drawn bird’s-eye view of an old European town, printed on thick board with a linen finish.",
                "https://images.unsplash.com/photo-1571195555904-f0fe9968ee5f?auto=format&fit=crop&w=800&h=600&q=80",
                27.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("gifts-13", "gifts", "Wildflower Honey", "The Larder Co",
                "Raw, unfiltered honey from hives among summer wildflowers, in a 340 g jar tied with twine.",
                "https://images.unsplash.com/photo-1587049352851-8d4e89133924?auto=format&fit=crop&w=800&h=600&q=80",
                18.50m, 14.80m, InStock: true, IsNew: false, GiftWrappable: true),
            new("gifts-14", "gifts", "Single-Origin Coffee Beans", "The Larder Co",
                "Whole beans from a single Ethiopian farm, roasted in small batches for notes of blueberry, jasmine and dark chocolate. 250 g.",
                "https://images.unsplash.com/photo-1695245503558-5cdb37f49092?auto=format&fit=crop&w=800&h=600&q=80",
                16.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
            new("gifts-15", "gifts", "Oak Hourglass Sand Timer", "Meridian & Co",
                "A 30-minute hourglass in hand-blown glass, set in a turned wooden frame, for focused work or a slow Sunday.",
                "https://images.unsplash.com/photo-1728677742573-a1295084f955?auto=format&fit=crop&w=800&h=600&q=80",
                74.00m, null, InStock: true, IsNew: false, GiftWrappable: false),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InsertProducts());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DeleteUnchangedProducts());
        }

        // Migrations run before the startup seed, so a brand-new database has no groups yet at this
        // point: the group check makes this a no-op there and leaves the seed to insert every
        // product from catalogue.json. A group an administrator has deleted is left empty, as the
        // admin API would leave it, and an id that is already taken (administrators choose product
        // ids) keeps its row rather than failing the migration or being overwritten.
        private static string InsertProducts() =>
            $"""
            INSERT INTO "Products" ({Columns}, "IsDeleted")
            SELECT v.*, FALSE
            FROM (VALUES
            {Values()}
            ) AS v({Columns})
            WHERE EXISTS (SELECT 1 FROM "ProductGroups" AS g WHERE g."Id" = v."GroupId" AND NOT g."IsDeleted")
            ON CONFLICT ("Id") DO NOTHING;
            """;

        // Only rows still exactly as Up inserted them are removed, so a product an administrator has
        // edited or deleted since keeps that change, the same rule RefreshCatalogueCopyAndImages
        // follows. Favourites of a removed product go with it (the foreign key cascades); order lines
        // carry their own copy of the product's name and price, so order history is unaffected.
        private static string DeleteUnchangedProducts() =>
            $"""
            DELETE FROM "Products" AS p
            USING (VALUES
            {Values()}
            ) AS v({Columns})
            WHERE p."Id" = v."Id"
                AND p."GroupId" = v."GroupId"
                AND p."Name" = v."Name"
                AND p."Brand" = v."Brand"
                AND p."Description" = v."Description"
                AND p."ImageUrl" = v."ImageUrl"
                AND p."Price" = v."Price"
                AND p."SalePrice" IS NOT DISTINCT FROM v."SalePrice"
                AND p."InStock" = v."InStock"
                AND p."IsNew" = v."IsNew"
                AND p."GiftWrappable" = v."GiftWrappable"
                AND NOT p."IsDeleted";
            """;

        private static string Values() =>
            string.Join(
                ",\n",
                Products.Select(p =>
                    $"({Literal(p.Id)}, {Literal(p.GroupId)}, {Literal(p.Name)}, {Literal(p.Brand)}, {Literal(p.Description)}, {Literal(p.ImageUrl)}, " +
                    $"{Number(p.Price)}, {(p.SalePrice is { } sale ? Number(sale) : "NULL")}, {Bool(p.InStock)}, {Bool(p.IsNew)}, {Bool(p.GiftWrappable)})"));

        // The values are the compile-time constants above, never user input; this only has to
        // double any single quote a name or description might contain.
        private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Bool(bool value) => value ? "TRUE" : "FALSE";

        private sealed record NewProduct(
            string Id,
            string GroupId,
            string Name,
            string Brand,
            string Description,
            string ImageUrl,
            decimal Price,
            decimal? SalePrice,
            bool InStock,
            bool IsNew,
            bool GiftWrappable);
    }
}
