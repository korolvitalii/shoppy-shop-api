using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShoppyShop.Infrastructure.Migrations
{
    /// <summary>
    /// Replaces the placeholder copy the catalogue was first seeded with (generated names such as
    /// "Refined Ceramic Table" filed under Beauty, random picsum.photos images) with product names,
    /// brands, descriptions and photos that match each other and their category. catalogue.json
    /// carries the same values, so a database upgraded in place ends up identical to one seeded
    /// fresh from that file. Ids, prices, stock and flags are untouched.
    /// </summary>
    public partial class RefreshCatalogueCopyAndImages : Migration
    {
        private const string SeededAccessoriesImage = "https://images.unsplash.com/photo-1523779917675-b6ed3a42a561?auto=format&fit=crop&w=1200&q=80";
        private const string RefreshedAccessoriesImage = "https://images.unsplash.com/photo-1724318497004-084cece3e7ae?auto=format&fit=crop&w=1200&q=80";

        private static readonly (string Id, ProductCopy Seeded, ProductCopy Refreshed)[] Products =
        [
            ("beauty-1",
                new("Refined Ceramic Table", "Heller Inc", "Discover the koala-like agility of our Table, perfect for metallic users", "https://picsum.photos/seed/shoppyshop-beauty-0/800/600"),
                new("Vetiver & Cedar Eau de Parfum", "Maison Arlet", "A dry, woody fragrance of vetiver, cedarwood and a hint of pink pepper, in a weighted glass flacon.", "https://images.unsplash.com/photo-1594125311687-3b1b3eafa9f4?auto=format&fit=crop&w=800&h=600&q=80")),
            ("beauty-2",
                new("Handcrafted Plastic Pants", "Ortiz - Leffler", "Modern Cheese designed with Steel for pink performance", "https://picsum.photos/seed/shoppyshop-beauty-1/800/600"),
                new("Professional Brush Collection, 12 Piece", "Loma Studio", "Twelve soft synthetic brushes for face and eyes, from a dense foundation buffer to a fine liner.", "https://images.unsplash.com/photo-1620464003286-a5b0d79f32c2?auto=format&fit=crop&w=800&h=600&q=80")),
            ("beauty-3",
                new("Gorgeous Ceramic Tuna", "Stark LLC", "Introducing the Serbia-inspired Chips, blending torn style with local craftsmanship", "https://picsum.photos/seed/shoppyshop-beauty-2/800/600"),
                new("Satin Lipstick in Bare Rose", "Loma Studio", "A creamy satin-finish lipstick in a soft rosy nude, housed in a weighted square case.", "https://images.unsplash.com/photo-1625093742435-6fa192b6fb10?auto=format&fit=crop&w=800&h=600&q=80")),
            ("beauty-4",
                new("Luxurious Aluminum Cheese", "Harber - Osinski", "Innovative Table featuring damp technology and Metal construction", "https://picsum.photos/seed/shoppyshop-beauty-3/800/600"),
                new("Orange Blossom Eau de Parfum", "Maison Arlet", "Luminous neroli and orange blossom over soft white musk, in a hand-faceted crystal bottle.", "https://images.unsplash.com/photo-1615108395437-df128ad79e80?auto=format&fit=crop&w=800&h=600&q=80")),
            ("beauty-5",
                new("Refined Rubber Shirt", "Wehner, Ryan and D'Amore", "Our zesty-inspired Hat brings a taste of luxury to your closed lifestyle", "https://picsum.photos/seed/shoppyshop-beauty-4/800/600"),
                new("Rose Quartz Facial Ritual Set", "Sollen Skin", "A rose quartz roller and gua sha stone paired with a nourishing facial oil for an at-home massage ritual.", "https://images.unsplash.com/photo-1600428877878-1a0fd85beda8?auto=format&fit=crop&w=800&h=600&q=80")),
            ("beauty-6",
                new("Intelligent Aluminum Gloves", "Schmeler, Altenwerth and Yundt", "The sleek and sudden Cheese comes with mint green LED lighting for smart functionality", "https://picsum.photos/seed/shoppyshop-beauty-5/800/600"),
                new("Overnight Renewal Gel Cream", "Sollen Skin", "A cooling gel moisturiser with hyaluronic acid and niacinamide that hydrates overnight without heaviness.", "https://images.unsplash.com/photo-1619451334792-150fd785ee74?auto=format&fit=crop&w=800&h=600&q=80")),
            ("beauty-7",
                new("Bespoke Bamboo Chips", "Oberbrunner, Schiller and Wiza", "Our flamingo-friendly Shoes ensures ripe comfort for your pets", "https://picsum.photos/seed/shoppyshop-beauty-6/800/600"),
                new("Botanical Facial Oil", "Fernmoor Botanics", "A lightweight blend of rosehip, jojoba and eucalyptus oils that absorbs quickly and leaves skin soft.", "https://images.unsplash.com/photo-1617897903246-719242758050?auto=format&fit=crop&w=800&h=600&q=80")),
            ("beauty-8",
                new("Frozen Rubber Shoes", "Luettgen Inc", "Professional-grade Car perfect for waterlogged training and recreational use", "https://picsum.photos/seed/shoppyshop-beauty-7/800/600"),
                new("Ionic Pro Hair Dryer", "Aerolume", "A lightweight, high-velocity dryer with ionic technology and three heat settings for fast, frizz-free styling.", "https://images.unsplash.com/photo-1727755868077-22f0d2ff8353?auto=format&fit=crop&w=800&h=600&q=80")),
            ("beauty-9",
                new("Unbranded Bronze Car", "Reinger - Grimes", "Innovative Computer featuring queasy technology and Granite construction", "https://picsum.photos/seed/shoppyshop-beauty-8/800/600"),
                new("Muted Nudes Nail Lacquer Set", "Loma Studio", "Four chip-resistant lacquers in taupe, sand, blush and berry with a glossy, gel-like finish.", "https://images.unsplash.com/photo-1602585578130-c9076e09330d?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-1",
                new("Gorgeous Concrete Hat", "Lubowitz - Kohler", "The sleek and baggy Salad comes with orange LED lighting for smart functionality", "https://picsum.photos/seed/shoppyshop-electronics-0/800/600"),
                new("Noise-Cancelling Wireless Headphones", "Solace Audio", "Adaptive noise cancelling, 30-hour battery life and memory-foam ear cushions for long listening sessions.", "https://images.unsplash.com/photo-1546435770-a3e426bf472b?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-2",
                new("Frozen Cotton Sausages", "D'Amore - Jones", "The sleek and big Bike comes with lavender LED lighting for smart functionality", "https://picsum.photos/seed/shoppyshop-electronics-1/800/600"),
                new("Compact Bluetooth Speaker", "Solace Audio", "A pocketable speaker with surprisingly full sound, a leather carry strap and 12 hours of playback.", "https://images.unsplash.com/photo-1582978571763-2d039e56f0c3?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-3",
                new("Fresh Plastic Chips", "Farrell, Schoen and Bednar", "The sleek and wasteful Fish comes with grey LED lighting for smart functionality", "https://picsum.photos/seed/shoppyshop-electronics-2/800/600"),
                new("Belt-Drive Turntable", "Northwave", "A two-speed belt-drive turntable with a precision aluminium tonearm and a built-in phono preamp.", "https://images.unsplash.com/photo-1603048588665-791ca8aea617?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-4",
                new("Incredible Gold Ball", "Beatty - Luettgen", "Professional-grade Shirt perfect for dirty training and recreational use", "https://picsum.photos/seed/shoppyshop-electronics-3/800/600"),
                new("Retro Compact Camera", "Veyra Optics", "A rangefinder-style compact with a 24-megapixel sensor, a fixed 23 mm lens and tactile manual dials.", "https://images.unsplash.com/photo-1516724562728-afc824a36e84?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-5",
                new("Oriental Marble Car", "Rempel Inc", "Introducing the Lao People's Democratic Republic-inspired Gloves, blending oval style with local craftsmanship", "https://picsum.photos/seed/shoppyshop-electronics-4/800/600"),
                new("Wireless Mechanical Keyboard", "Keystead", "A compact 75% layout with hot-swappable tactile switches, multi-device Bluetooth and a Mac/Windows toggle.", "https://images.unsplash.com/photo-1618384887929-16ec33fab9ef?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-6",
                new("Tasty Metal Bacon", "Dicki Inc", "Ergonomic Table made with Plastic for all-day dismal support", "https://picsum.photos/seed/shoppyshop-electronics-5/800/600"),
                new("Glare-Free E-Reader", "Pagewell", "A 6-inch glare-free E Ink display with adjustable warm light and weeks of battery life.", "https://images.unsplash.com/photo-1594498257673-9f36b767286c?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-7",
                new("Handmade Granite Chicken", "Wisoky, Hartmann and Feest", "Modern Tuna designed with Metal for impossible performance", "https://picsum.photos/seed/shoppyshop-electronics-6/800/600"),
                new("True Wireless Earbuds", "Solace Audio", "Compact earbuds with touch controls, a pocket-sized charging case and 28 hours of total playback.", "https://images.unsplash.com/photo-1606220588913-b3aacb4d2f46?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-8",
                new("Awesome Silk Bacon", "Yost, Strosin and Mante", "Stylish Tuna designed to make you stand out with functional looks", "https://picsum.photos/seed/shoppyshop-electronics-7/800/600"),
                new("Minimalist Smartwatch", "Veyra Optics", "A round always-on display, heart-rate and sleep tracking, and a soft silicone strap in a clean design.", "https://images.unsplash.com/photo-1523275335684-37898b6baf30?auto=format&fit=crop&w=800&h=600&q=80")),
            ("electronics-9",
                new("Elegant Plastic Mouse", "Crist LLC", "The Eugene Bike is the latest in a series of sweet products from Armstrong, Dickinson and Schultz", "https://picsum.photos/seed/shoppyshop-electronics-8/800/600"),
                new("Multi-Room Wi-Fi Speaker", "Northwave", "Room-filling 360° sound, voice control and Wi-Fi streaming that groups with other speakers around the home.", "https://images.unsplash.com/photo-1507878566509-a0dbe19677a5?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-1",
                new("Licensed Silk Salad", "Bauch, Jacobs and Denesik", "Savor the sour essence in our Mouse, designed for peaceful culinary adventures", "https://picsum.photos/seed/shoppyshop-fashion-0/800/600"),
                new("Leather Biker Jacket", "Atelier Norde", "A classic asymmetric-zip biker in supple black lambskin, with snap lapels and zipped cuffs.", "https://images.unsplash.com/photo-1551028719-00167b16eac5?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-2",
                new("Ergonomic Marble Cheese", "Hodkiewicz-Ratke - Kilback", "New plum Cheese with ergonomic design for energetic comfort", "https://picsum.photos/seed/shoppyshop-fashion-1/800/600"),
                new("Brushed Cotton Crewneck Sweatshirt", "Fieldhouse", "A relaxed crewneck in heavyweight organic cotton, brushed on the inside for softness.", "https://images.unsplash.com/photo-1620799140408-edc6dcb6d633?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-3",
                new("Bespoke Rubber Pants", "Rolfson LLC", "Featuring Phosphorus-enhanced technology, our Ball offers unparalleled splendid performance", "https://picsum.photos/seed/shoppyshop-fashion-2/800/600"),
                new("Dotted Chambray Shirt", "Fieldhouse", "A soft chambray shirt with a fine white dot print, button cuffs and a relaxed fit.", "https://images.unsplash.com/photo-1596755094514-f87e34085b2c?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-4",
                new("Gorgeous Gold Computer", "D'Amore and Sons", "Innovative Fish featuring unfortunate technology and Concrete construction", "https://picsum.photos/seed/shoppyshop-fashion-3/800/600"),
                new("Camel Wool Wrap Coat", "Atelier Norde", "A belted wrap coat in a warm wool blend with dropped shoulders and a mid-calf length.", "https://images.unsplash.com/photo-1539533018447-63fcce2678e3?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-5",
                new("Modern Gold Table", "Kerluke, Crona and Crist", "Introducing the Slovakia-inspired Chips, blending hot style with local craftsmanship", "https://picsum.photos/seed/shoppyshop-fashion-4/800/600"),
                new("Heritage Leather Work Boots", "Cobble & Last", "Full-grain leather boots with speed hooks, waxed laces and a Goodyear-welted lug sole.", "https://images.unsplash.com/photo-1520639888713-7851133b1ed0?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-6",
                new("Recycled Gold Chips", "Heidenreich - Wilderman", "Our frog-friendly Soap ensures filthy comfort for your pets", "https://picsum.photos/seed/shoppyshop-fashion-5/800/600"),
                new("Check Wool-Blend Blazer", "Atelier Norde", "An oversized double-breasted blazer in a grey Prince of Wales check.", "https://images.unsplash.com/photo-1608234808654-2a8875faa7fd?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-7",
                new("Rustic Steel Pants", "Johnston-Witting and Sons", "The Francesca Hat is the latest in a series of gentle products from Hahn Group", "https://picsum.photos/seed/shoppyshop-fashion-6/800/600"),
                new("Cotton Field Jacket", "Fieldhouse", "A roomy olive field jacket in washed cotton canvas with patch pockets and a stand collar.", "https://images.unsplash.com/photo-1544022613-e87ca75a784a?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-8",
                new("Licensed Steel Fish", "Hessel, Williamson and Nicolas", "Discover the rabbit-like agility of our Soap, perfect for outgoing users", "https://picsum.photos/seed/shoppyshop-fashion-7/800/600"),
                new("Leather Derby Shoes", "Cobble & Last", "Hand-burnished tan leather derbies with a subtly perforated upper and a leather sole.", "https://images.unsplash.com/photo-1614252235316-8c857d38b5f4?auto=format&fit=crop&w=800&h=600&q=80")),
            ("fashion-9",
                new("Tasty Bronze Soap", "Effertz - Keebler", "Discover the turtle-like agility of our Bike, perfect for pink users", "https://picsum.photos/seed/shoppyshop-fashion-8/800/600"),
                new("Floral Belted Midi Dress", "Wren", "A flowing midi dress in a bold floral print, with flutter sleeves and a woven belt.", "https://images.unsplash.com/photo-1572804013309-59a88b7e92f1?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-1",
                new("Bespoke Concrete Shoes", "Gutkowski, Johnson and Kertzmann", "Discover the improbable new Hat with an exciting mix of Bronze ingredients", "https://picsum.photos/seed/shoppyshop-home-0/800/600"),
                new("Velvet Three-Seater Sofa", "Stillhouse", "A mid-century-inspired sofa in deep green velvet with plump cushions and tapered wooden legs.", "https://images.unsplash.com/photo-1555041469-a586c61ea9bc?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-2",
                new("Handcrafted Silk Shoes", "Wyman - Zemlak", "Our bee-friendly Sausages ensures tough comfort for your pets", "https://picsum.photos/seed/shoppyshop-home-1/800/600"),
                new("Linen Swivel Lounge Chair", "Stillhouse", "A sculpted lounge chair upholstered in textured linen, on a smooth 360° swivel base.", "https://images.unsplash.com/photo-1580480055273-228ff5388ef8?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-3",
                new("Awesome Concrete Sausages", "Wilkinson, Sporer and Little", "The Enhanced analyzing budgetary management Pizza offers reliable performance and simplistic design", "https://picsum.photos/seed/shoppyshop-home-2/800/600"),
                new("Industrial Floor Lamp", "Brightwork", "A steel floor lamp with an adjustable dome shade and a matte graphite finish.", "https://images.unsplash.com/photo-1507473885765-e6ed057f782c?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-4",
                new("Oriental Metal Chips", "Lehner, Stehr and Frami", "Introducing the Afghanistan-inspired Chips, blending anguished style with local craftsmanship", "https://picsum.photos/seed/shoppyshop-home-3/800/600"),
                new("Brushed Brass Dome Pendant", "Brightwork", "A spun-metal dome pendant in brushed brass that casts a warm, focused glow. Hang one alone or in a cluster.", "https://images.unsplash.com/photo-1540932239986-30128078f3c5?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-5",
                new("Bespoke Concrete Pants", "Bogisich LLC", "Ergonomic Keyboard made with Rubber for all-day warped support", "https://picsum.photos/seed/shoppyshop-home-4/800/600"),
                new("Moulded Dining Chairs, Set of 2", "Stillhouse", "Moulded shell seats with a cushioned pad on solid beech legs, easy to wipe down and comfortable for long dinners.", "https://images.unsplash.com/photo-1592078615290-033ee584e267?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-6",
                new("Small Plastic Towels", "Padberg, Fahey and Frami", "Introducing the Costa Rica-inspired Soap, blending kaleidoscopic style with local craftsmanship", "https://picsum.photos/seed/shoppyshop-home-5/800/600"),
                new("Vintage-Wash Area Rug", "Loomward", "A traditional medallion pattern in soft, faded tones with a low pile that suits busy rooms.", "https://images.unsplash.com/photo-1600166898405-da9535204843?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-7",
                new("Awesome Ceramic Shoes", "Emard LLC", "Stylish Soap designed to make you stand out with ruddy looks", "https://picsum.photos/seed/shoppyshop-home-6/800/600"),
                new("Reclaimed Wood Bedside Table", "Oak & Ember", "A chunky bedside table made from reclaimed timber, with an open shelf for books.", "https://images.unsplash.com/photo-1532372320572-cda25653a26d?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-8",
                new("Small Concrete Soap", "Thompson, Weber and Satterfield", "Introducing the Germany-inspired Shoes, blending essential style with local craftsmanship", "https://picsum.photos/seed/shoppyshop-home-7/800/600"),
                new("Speckled Stoneware Cups, Set of 4", "Kiln Street", "Hand-thrown stoneware cups with a speckled cream glaze and a raw clay base.", "https://images.unsplash.com/photo-1610701596007-11502861dcfa?auto=format&fit=crop&w=800&h=600&q=80")),
            ("home-9",
                new("Rustic Aluminum Fish", "Parisian - Ziemann", "Smitham LLC's most advanced Mouse technology increases primary capabilities", "https://picsum.photos/seed/shoppyshop-home-8/800/600"),
                new("Stoneware Bud Vases, Set of 3", "Kiln Street", "Three bottle-neck vases in a grey speckled glaze, sized for dried stems or a single fresh bloom.", "https://images.unsplash.com/photo-1565193566173-7a0ee3dbe261?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-1",
                new("Handmade Steel Sausages", "Tromp - Romaguera", "The Open-source static local area network Bike offers reliable performance and powerless design", "https://picsum.photos/seed/shoppyshop-accessories-0/800/600"),
                new("Round Metal Sunglasses", "Ostra Eyewear", "Slim gold-tone round frames with polarised lenses and full UV400 protection.", "https://images.unsplash.com/photo-1511499767150-a48a237f0083?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-2",
                new("Soft Bronze Mouse", "Balistreri Group", "Introducing the Equatorial Guinea-inspired Salad, blending circular style with local craftsmanship", "https://picsum.photos/seed/shoppyshop-accessories-1/800/600"),
                new("Leather Messenger Bag", "Tanner & Vale", "A vintage-finish leather messenger with twin buckle straps, two front pockets and a padded laptop sleeve.", "https://images.unsplash.com/photo-1473188588951-666fce8e7c68?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-3",
                new("Soft Wooden Sausages", "Torp, Ullrich-Mills and Kris", "Our peacock-friendly Chair ensures fuzzy comfort for your pets", "https://picsum.photos/seed/shoppyshop-accessories-2/800/600"),
                new("Minimalist Leather-Strap Watch", "Hourline", "A clean white dial in a slim rose-gold-tone case on a soft taupe leather strap.", "https://images.unsplash.com/photo-1524592094714-0f0654e20314?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-4",
                new("Incredible Rubber Fish", "Koepp, Strosin and Goodwin-Schmitt", "Featuring Krypton-enhanced technology, our Car offers unparalleled soggy performance", "https://picsum.photos/seed/shoppyshop-accessories-3/800/600"),
                new("Leather Crossbody Bag", "Tanner & Vale", "A structured tan crossbody with a fold-over flap, magnetic closure and adjustable strap.", "https://images.unsplash.com/photo-1600857062241-98e5dba7f214?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-5",
                new("Fresh Marble Gloves", "Shanahan LLC", "Discover the unfit new Pants with an exciting mix of Granite ingredients", "https://picsum.photos/seed/shoppyshop-accessories-4/800/600"),
                new("Leather Backpack", "Tanner & Vale", "A hand-finished leather backpack with a zipped front pocket and room for a 15-inch laptop.", "https://images.unsplash.com/photo-1622560480605-d83c853bc5c3?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-6",
                new("Sleek Bamboo Bacon", "Kling, Lesch and VonRueden", "Ergonomic Fish made with Wooden for all-day fluffy support", "https://picsum.photos/seed/shoppyshop-accessories-5/800/600"),
                new("Chunky Chain Bracelet", "Maren Jewellery", "A bold chain bracelet in 18-carat gold vermeil with a secure lobster clasp.", "https://images.unsplash.com/photo-1602173574767-37ac01994b2a?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-7",
                new("Modern Granite Soap", "Emmerich Inc", "The Shaun Computer is the latest in a series of infatuated products from Thompson - Murazik", "https://picsum.photos/seed/shoppyshop-accessories-6/800/600"),
                new("Freshwater Pearl Necklace", "Maren Jewellery", "Hand-knotted freshwater pearls with a sterling silver clasp, presented in a gift box.", "https://images.unsplash.com/photo-1515562141207-7a88fb7ce338?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-8",
                new("Unbranded Bronze Bike", "Anderson LLC", "Innovative Table featuring impolite technology and Wooden construction", "https://picsum.photos/seed/shoppyshop-accessories-7/800/600"),
                new("Leather Bifold Wallet", "Tanner & Vale", "A slim bifold in vegetable-tanned leather with six card slots that softens and darkens with use.", "https://images.unsplash.com/photo-1627123424574-724758594e93?auto=format&fit=crop&w=800&h=600&q=80")),
            ("accessories-9",
                new("Ergonomic Bamboo Salad", "Nicolas, Reynolds and Haag", "Discover the bear-like agility of our Car, perfect for babyish users", "https://picsum.photos/seed/shoppyshop-accessories-8/800/600"),
                new("Halo Cocktail Ring", "Maren Jewellery", "A sterling silver ring set with a round white topaz framed by a halo of sparkling stones.", "https://images.unsplash.com/photo-1605100804763-247f67b3557e?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-1",
                new("Refined Plastic Tuna", "Hoppe Inc", "Our polar bear-friendly Tuna ensures gentle comfort for your pets", "https://picsum.photos/seed/shoppyshop-gifts-0/800/600"),
                new("Artisan Chocolate Selection", "Cocoa Row", "Twenty hand-finished truffles and pralines in dark, milk and white chocolate.", "https://images.unsplash.com/photo-1481391319762-47dff72954d9?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-2",
                new("Incredible Metal Pizza", "Littel, Effertz and Labadie", "Stylish Tuna designed to make you stand out with these looks", "https://picsum.photos/seed/shoppyshop-gifts-1/800/600"),
                new("Wooden Chess Set", "Rook & Rank", "A hardwood board with inlaid squares and weighted, hand-turned pieces.", "https://images.unsplash.com/photo-1646109324562-aab0ec63a54f?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-3",
                new("Rustic Granite Computer", "Hickle and Sons", "New orange Fish with ergonomic design for humiliating comfort", "https://picsum.photos/seed/shoppyshop-gifts-2/800/600"),
                new("Antique-Style Desk Globe", "Meridian & Co", "A 30 cm globe with antique-style cartography on a turned wooden base.", "https://images.unsplash.com/photo-1594450281353-5a7a067358cc?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-4",
                new("Generic Aluminum Soap", "Hessel, Feeney and Dooley", "The sleek and hollow Computer comes with mint green LED lighting for smart functionality", "https://picsum.photos/seed/shoppyshop-gifts-3/800/600"),
                new("Porcelain Tea Set for One", "Kiln Street", "A white porcelain teapot with a woven cane handle and a matching cup for a calm tea ritual.", "https://images.unsplash.com/photo-1564890369478-c89ca6d9cde9?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-5",
                new("Frozen Rubber Computer", "Abernathy - O'Reilly", "Experience the red brilliance of our Shoes, perfect for vast environments", "https://picsum.photos/seed/shoppyshop-gifts-4/800/600"),
                new("Amber & Oud Scented Candle", "Hearth & Wick", "A slow-burning soy wax candle with notes of amber, oud and vanilla, in a reusable glass vessel.", "https://images.unsplash.com/photo-1603006905003-be475563bc59?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-6",
                new("Refined Plastic Towels", "Schaden - Crooks", "Discover the whale-like agility of our Pizza, perfect for vague users", "https://picsum.photos/seed/shoppyshop-gifts-5/800/600"),
                new("Juniper Bonsai Tree", "Little Grove", "A hand-styled juniper bonsai in a glazed ceramic pot, with a care guide for first-time growers.", "https://images.unsplash.com/photo-1729111146336-f1c5cbf6122a?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-7",
                new("Fresh Ceramic Hat", "Padberg-Jerde - Olson", "Generic Ball designed with Aluminum for responsible performance", "https://picsum.photos/seed/shoppyshop-gifts-6/800/600"),
                new("Picnic Hamper for Two", "The Larder Co", "A lidded willow hamper with leather fastenings, packed with a cheese board and tableware for two.", "https://images.unsplash.com/photo-1596241913274-fd9f65e3a2b5?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-8",
                new("Recycled Bronze Towels", "Lemke - Connelly", "Our giraffe-friendly Chicken ensures caring comfort for your pets", "https://picsum.photos/seed/shoppyshop-gifts-7/800/600"),
                new("Fountain Pen & Notebook Set", "Quire & Nib", "A lacquered fountain pen with a steel nib, paired with a lay-flat notebook of ink-friendly paper.", "https://images.unsplash.com/photo-1517842645767-c639042777db?auto=format&fit=crop&w=800&h=600&q=80")),
            ("gifts-9",
                new("Modern Ceramic Tuna", "McGlynn, Rohan and Bauch", "The Streamlined interactive data-warehouse Table offers reliable performance and forceful design", "https://picsum.photos/seed/shoppyshop-gifts-8/800/600"),
                new("Heirloom Aviator Teddy Bear", "Bramble & Bear", "A fully jointed teddy in aviator goggles and a knitted scarf, made to be treasured for years.", "https://images.unsplash.com/photo-1530325553241-4f6e7690cf36?auto=format&fit=crop&w=800&h=600&q=80")),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReplaceProductCopy(Products.Select(x => (x.Id, x.Seeded, x.Refreshed))));
            migrationBuilder.Sql(ReplaceGroupImage("accessories", SeededAccessoriesImage, RefreshedAccessoriesImage));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReplaceProductCopy(Products.Select(x => (x.Id, x.Refreshed, x.Seeded))));
            migrationBuilder.Sql(ReplaceGroupImage("accessories", RefreshedAccessoriesImage, SeededAccessoriesImage));
        }

        // A row is only rewritten while it still holds exactly the copy it is being moved away from.
        // Products are editable through the admin API, so anything an administrator has changed
        // since the seed keeps their text and image, in either direction, rather than being
        // silently reverted to seed data.
        private static string ReplaceProductCopy(IEnumerable<(string Id, ProductCopy From, ProductCopy To)> rows)
        {
            string values = string.Join(
                ",\n",
                rows.Select(row =>
                    $"({Literal(row.Id)}, {Literal(row.From.Name)}, {Literal(row.From.Brand)}, {Literal(row.From.Description)}, {Literal(row.From.ImageUrl)}, " +
                    $"{Literal(row.To.Name)}, {Literal(row.To.Brand)}, {Literal(row.To.Description)}, {Literal(row.To.ImageUrl)})"));

            return $"""
                UPDATE "Products" AS p
                SET "Name" = v."Name", "Brand" = v."Brand", "Description" = v."Description", "ImageUrl" = v."ImageUrl"
                FROM (VALUES
                {values}
                ) AS v("Id", "FromName", "FromBrand", "FromDescription", "FromImageUrl", "Name", "Brand", "Description", "ImageUrl")
                WHERE p."Id" = v."Id"
                    AND p."Name" = v."FromName"
                    AND p."Brand" = v."FromBrand"
                    AND p."Description" = v."FromDescription"
                    AND p."ImageUrl" = v."FromImageUrl";
                """;
        }

        private static string ReplaceGroupImage(string groupId, string from, string to) =>
            $"""
            UPDATE "ProductGroups" SET "ImageUrl" = {Literal(to)}
            WHERE "Id" = {Literal(groupId)} AND "ImageUrl" = {Literal(from)};
            """;

        // The values are the compile-time constants above, never user input; this only has to
        // double the single quotes that some of the seeded brands contain (e.g. "D'Amore").
        private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        private sealed record ProductCopy(string Name, string Brand, string Description, string ImageUrl);
    }
}
