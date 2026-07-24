using Tower.Core.Bills;
using Xunit;

public class BillParserTests
{
    // Trip receipt: Estimated Fare + Surge near the top must be ignored; "Paid Amount" is the charge (incl. tip).
    const string TripBody =
        "Trip ID - 1458530325 Estimated Fare LKR 308.88 Surge LKR +100.00 " +
        "Total Trip Fare LKR 408.88 Paid Amount LKR 408.88 FriMi";

    // Delivery receipt: &nbsp; entity between LKR and number, and a payment method ("Card")
    // sits between "Paid by" and the amount — this is the real wording from live receipts.
    const string DeliveryBody =
        "Order ID - 178671638 Sub Total LKR&nbsp;2970.00 Delivery Fee +LKR&nbsp;159.00 " +
        "Temporary Fuel Surcharge +LKR&nbsp;29.00 Total LKR&nbsp;3158.00 Paid by Card LKR&nbsp;3158.00";

    [Fact]
    public void Trip_uses_paid_amount_not_estimate()
    {
        var r = BillParser.TryParse("support@pickme.lk", "PickMe | Email Receipt for Trip ID 1458530325", TripBody);
        Assert.NotNull(r);
        Assert.Equal("PickMe Trip", r!.Value.Profile.Name);
        Assert.Equal("Transportation", r.Value.Profile.Category);
        Assert.Equal(408.88m, r.Value.Amount);
    }

    [Fact]
    public void Delivery_handles_nbsp_and_maps_to_food()
    {
        var r = BillParser.TryParse("support@pickme.lk", "PickMe | Delivery Email Receipt for - 178671638", DeliveryBody);
        Assert.NotNull(r);
        Assert.Equal("PickMe Delivery", r!.Value.Profile.Name);
        Assert.Equal("Food", r.Value.Profile.Category);
        Assert.Equal(3158.00m, r.Value.Amount);
    }

    // Keells e-bill: "Total Net Amount" (net of discounts) with no currency prefix on the number.
    const string KeellsBody =
        "Bill No : 4300423 Rs: 233.00 Total Gross Amount 4,602.00 Total Net Amount 4,369.00 Credit Card 4,369.00";

    [Fact]
    public void Keells_uses_total_net_amount_and_maps_to_grocery()
    {
        var r = BillParser.TryParse("web.jms@keells.com", "Keells E-Bill | 07-Jul-2026 at Keells - Pitakotte 2 | 4300423", KeellsBody);
        Assert.NotNull(r);
        Assert.Equal("Keells E-Bill", r!.Value.Profile.Name);
        Assert.Equal("Grocery", r.Value.Profile.Category);
        Assert.Equal(4369.00m, r.Value.Amount);
    }

    [Fact]
    public void Keells_old_bill_subject_variant_also_matches()
    {
        var body = "Bill No : 1006973 Total Gross Amount 2,600.00 Total Net Amount 2,600.00 Credit Card - SCB 2,600.00";
        var r = BillParser.TryParse("web.jms@keells.com", "Keells Bill - 1006973", body);
        Assert.NotNull(r);
        Assert.Equal("Grocery", r!.Value.Profile.Category);
        Assert.Equal(2600.00m, r.Value.Amount);
    }

    // Older PickMe trip template: no "Paid Amount"; falls back to "Total trip fare".
    [Fact]
    public void Pre2021_trip_templates_parse()
    {
        // ~2018: "Fare Amount LKR 140.13"
        var t2018 = BillParser.TryParse("support@pickme.lk", "PickMe | Email Receipt for Trip ID 127061606",
            "Driver ... Fare Amount LKR 140.13 TOTAL DISTANCE 3.2 km");
        Assert.Equal(140.13m, t2018!.Value.Amount);
        Assert.Equal("Transportation", t2018.Value.Profile.Category);

        // ~2016: "TOTAL FARE Rs. 152.63" / "Paid by Cash Rs. 152.63"
        var t2016 = BillParser.TryParse("support@pickme.lk", "PickMe | Email Receipt for Trip ID 53073",
            "TOTAL FARE Rs. 152.63 Paid by Cash Rs. 152.63 DRIVER RATING");
        Assert.Equal(152.63m, t2016!.Value.Amount);
    }

    [Fact]
    public void Old_trip_falls_back_to_total_trip_fare()
    {
        var body = "Trip ID - 327372273 Total LKR 145.88 Total trip fare LKR 145.88 PAID BY **** LKR 145.88";
        var r = BillParser.TryParse("support@pickme.lk", "PickMe | Email Receipt for Trip ID 327372273", body);
        Assert.NotNull(r);
        Assert.Equal("Transportation", r!.Value.Profile.Category);
        Assert.Equal(145.88m, r.Value.Amount);
    }

    [Fact]
    public void Daraz_order_uses_total_inclusive_of_tax_and_maps_to_online_shopping()
    {
        var body = "Subtotal: Rs 600.00 Shipping fee: Rs 345.00 Total Saving: Rs (0.00) " +
                   "Total (inclusive of tax, if any): Rs 960.00 Paid by: Credit or Debit Card";
        var r = BillParser.TryParse("noreply@support.daraz.lk", "Yay, your Order 225252071710771 is confirmed!", body);
        Assert.NotNull(r);
        Assert.Equal("Daraz Order", r!.Value.Profile.Name);
        Assert.Equal("Online Shopping", r.Value.Profile.Category);
        Assert.Equal(960.00m, r.Value.Amount);
    }

    [Fact]
    public void Daraz_older_placed_templates_parse()
    {
        // "…has been placed!" (2024): grand total is "Total Payment (VAT Incl)"; note the "Total:" subtotal must be ignored.
        var body2024 = "Total: Rs 1104 Delivery Fee: Rs 450 Total Discount: Rs 22 Total Payment (VAT Incl): Rs 1532";
        var r1 = BillParser.TryParse("noreply@support.daraz.lk", "Your order has been placed!", body2024);
        Assert.NotNull(r1);
        Assert.Equal(1532m, r1!.Value.Amount);

        // "…is placed!" (2022): plain "Total Rs 1723" (price + delivery), no decimals.
        var body2022 = "Price Rs 1439 Discount Rs (0) Delivery fee Rs 284 Total Rs 1723 Shipping Option STANDARD";
        var r2 = BillParser.TryParse("noreply@support.daraz.lk", "Hey John Samarasinghe, your order is placed!", body2022);
        Assert.NotNull(r2);
        Assert.Equal("Online Shopping", r2!.Value.Profile.Category);
        Assert.Equal(1723m, r2.Value.Amount);
    }

    [Fact]
    public void PizzaHut_uses_total_amount_and_maps_to_food()
    {
        var body = "ORDER PLACED SUCCESSFULLY Order No B003532601 Sub Total 6,320.00 Delivery 474.00 Total Amount 6,794.00";
        var r = BillParser.TryParse("online.phsl@gamma.lk", "Online Order Confirmation - B003532601 @ Pizza Hut - Sri Lanka", body);
        Assert.NotNull(r);
        Assert.Equal("Food", r!.Value.Profile.Category);
        Assert.Equal(6794.00m, r.Value.Amount);
    }

    [Fact]
    public void AliExpress_uses_order_total_and_maps_to_online_shopping()
    {
        var body = "Banana Plug x1 Order total LKR 42,200.61 Payment method Visa";
        var r = BillParser.TryParse("transaction@notice.aliexpress.com", "Order 1118358739575896: order confirmed", body);
        Assert.NotNull(r);
        Assert.Equal("Online Shopping", r!.Value.Profile.Category);
        Assert.Equal(42200.61m, r.Value.Amount);
    }

    [Fact]
    public void Dialog_bills_are_pdf_profiles_reading_total_charges_for_bill_period()
    {
        // "Total Charges for Bill Period" is the charge; the "Total Amount Payable" carry-over
        // balance next to it must never be picked up instead.
        var pdfText = "MOBILE NUMBER: 114103678 BILL PERIOD 15/12/2024 - 14/01/2025 " +
                      "Total Charges for Bill Period 4,469.70 Total Amount Payable -59.09";

        var p = BillParser.Match("ebill@dialog.lk", "Dialog Fixed_Solutions E-Bill for the month of Jan-2025 - 70073153");
        Assert.True(p!.FromPdf);
        Assert.Equal(4469.70m, BillParser.ExtractAmount(p, pdfText)!.Value.Amount);
        Assert.Equal("Home Broadband", p.CategoryFrom!(pdfText));
    }

    [Fact]
    public void Foreign_currency_receipts_keep_their_currency()
    {
        var a = BillParser.TryParse("invoice+statements@mail.anthropic.com", "Your receipt from Anthropic, PBC #2936-6462-4103",
            "Claude Pro Qty 1 $20.00 Total $20.00 Amount paid $20.00");
        Assert.NotNull(a);
        Assert.Equal("AI", a!.Value.Profile.Category);
        Assert.Equal("USD", a.Value.Profile.Currency);
        Assert.Equal(20.00m, a.Value.Amount);

        var g = BillParser.TryParse("noreply@github.com", "[GitHub] Payment Receipt for iamJohnnySam",
            "GitHub Copilot Pro - month: $7.67 Tax: $0.00 USD Total: $7.67");
        Assert.NotNull(g);
        Assert.Equal("USD", g!.Value.Profile.Currency);
        Assert.Equal(7.67m, g.Value.Amount);

        var n = BillParser.TryParse("npc@nets.com.sg", "NPC",
            "A total of $5.20 is deducted from card number ending 8618 incurred for Bus/MRT rides.");
        Assert.NotNull(n);
        Assert.Equal("Transport", n!.Value.Profile.Category);
        Assert.Equal("SGD", n.Value.Profile.Currency);
        Assert.Equal(5.20m, n.Value.Amount);
    }

    [Fact]
    public void PickMe_membership_maps_to_membership()
    {
        var body = "PickMe PASS. 1 Month LKR 549.00 Start date 13 December 2025 Total LKR 549.00 Paid Amount LKR 549.00";
        var r = BillParser.TryParse("support@pickme.lk", "Membership Renewal Receipt", body);
        Assert.NotNull(r);
        Assert.Equal("Membership", r!.Value.Profile.Category);
        Assert.Equal(549.00m, r.Value.Amount);
    }

    [Fact]
    public void Keells_order_confirmation_uses_total_amount_rs_and_maps_to_groceries()
    {
        var body = "Gross Amount (Rs.) 5,134.50 Total Discount (Rs.) 95.00 Transport Amount (Rs.) 150.00 Total Amount (Rs.) 5,189.50";
        var r = BillParser.TryParse("web.jms@keells.com", "Keells Order Confirmation | 12-09-2021 | Borella | ITO1096190", body);
        Assert.NotNull(r);
        Assert.Equal("Groceries", r!.Value.Profile.Category);
        Assert.Equal(5189.50m, r.Value.Amount);
    }

    [Fact]
    public void GooglePlay_parses_lkr_total_with_sinhala_mark()
    {
        var body = "Item Price 100 GB (Google One) Subscription Total : රු. 5,750.00/year (Includes VAT)";
        var r = BillParser.TryParse("googleplay-noreply@google.com", "Your Google Play Order Receipt from Nov 17, 2025", body);
        Assert.NotNull(r);
        Assert.Equal("Apps & Subscriptions", r!.Value.Profile.Category);
        Assert.Equal("LKR", r.Value.Profile.Currency);
        Assert.Equal(5750.00m, r.Value.Amount);
    }

    [Fact]
    public void Dominos_uses_grand_total_and_maps_to_food()
    {
        var body = "Sub Total : Rs.6,870.19 Taxes & Charges : Rs.1,201.28 Grand Total : Rs.7,224.00";
        var r = BillParser.TryParse("do-not-reply@dominos.co.in", "Order Successful", body);
        Assert.NotNull(r);
        Assert.Equal("Food", r!.Value.Profile.Category);
        Assert.Equal(7224.00m, r.Value.Amount);
    }

    [Fact]
    public void PayHere_gateway_splits_by_merchant_subject()
    {
        var d = BillParser.TryParse("receipts@mail.payhere.lk", "Dominos Pizza Sri Lanka Payment Receipt #320048107474",
            "Item Qty Amount PIZZA LKR 6,325.00 Subtotal LKR 6,325.00 Total LKR 6,325.00");
        Assert.Equal("Food", d!.Value.Profile.Category);
        Assert.Equal(6325.00m, d.Value.Amount);

        var r = BillParser.TryParse("receipts@mail.payhere.lk", "Riyasewana Lanka Private Limited Payment Receipt #320045996094",
            "Subtotal LKR 1,000.00 Total LKR 1,000.00");
        Assert.Equal("Ads", r!.Value.Profile.Category);
        Assert.Equal(1000.00m, r.Value.Amount);
    }

    [Fact]
    public void Namecheap_skips_zero_subtotal_takes_total_usd()
    {
        var body = "Initial Charge: $14.98 Sub Total $0.00 TOTAL $14.98";
        var r = BillParser.TryParse("support@namecheap.com", "Namecheap Order Summary (Order# 200087354);", body);
        Assert.NotNull(r);
        Assert.Equal("Website", r!.Value.Profile.Category);
        Assert.Equal("USD", r.Value.Profile.Currency);
        Assert.Equal(14.98m, r.Value.Amount);
    }

    [Fact]
    public void Other_new_profiles_parse()
    {
        var p = BillParser.TryParse("receipts@payablepayments.lk", "Invoice for your Order from Lexi Pro Service", "TOTAL AMOUNT : LKR 1,850.00");
        Assert.Equal("Home", p!.Value.Profile.Category); Assert.Equal(1850.00m, p.Value.Amount);

        var k = BillParser.TryParse("mail@kandos.lk", "Kandos.lk - Order Confirmation #KAN2603240002",
            "Subtotal Rs 3,000.00 Total Amount Rs 3,000.00 Paid Amount Rs 3,000.00 Balance Due Rs 0.00");
        Assert.Equal("Groceries", k!.Value.Profile.Category); Assert.Equal(3000.00m, k.Value.Amount);

        var e = BillParser.TryParse("receipt@echannelling.com", "Welcome to eChanneling - eRewards",
            "CHANNELLING DONE Total Fee : 114.00 LKR Breakdown Doctor Fee: 0.00 /= Booking Fee:114.00 /=");
        Assert.Equal("e-Channeling", e!.Value.Profile.Category); Assert.Equal(114.00m, e.Value.Amount);

        var m = BillParser.TryParse("support@pickme.lk", "Membership Payment Invoice", "PickMe PASS Total LKR 549.00 Paid Amount LKR 549.00");
        Assert.Equal("Membership", m!.Value.Profile.Category); Assert.Equal(549.00m, m.Value.Amount);
    }

    [Fact]
    public void AliExpress_detects_currency_per_email()
    {
        var usd = BillParser.TryParse("transaction@notice.aliexpress.com", "Order 1113362589405896: order confirmed",
            "Cable x1 Order total US $9.15 See order details");
        Assert.NotNull(usd);
        Assert.Equal(9.15m, usd!.Value.Amount);
        Assert.Equal("USD", usd.Value.Currency);   // detected from "US $"

        var lkr = BillParser.TryParse("transaction@notice.aliexpress.com", "Order 111: order confirmed",
            "Item x1 Order total LKR 42,200.61 Payment");
        Assert.Equal("LKR", lkr!.Value.Currency);
    }

    [Fact]
    public void Doc990_is_pdf_profile_and_reads_total_charges_from_pdf_text()
    {
        var profile = BillParser.Match("no-reply@doc.lk", "Doc990 BOOKING RECEIPT");
        Assert.NotNull(profile);
        Assert.True(profile!.FromPdf);
        Assert.Equal("e-Channeling", profile.Category);

        // The email body has no amount (it's an image) — extraction runs on the PDF text instead.
        var pdfText = "HOSPITAL CHARGES : 1500.00 LKR DOCTOR CHARGES : 2000.00 LKR " +
                      "BOOKING CHARGES : 381.65 LKR TOTAL CHARGES : 3881.65 LKR (67.35 LKR Discounted)";
        var e = BillParser.ExtractAmount(profile, pdfText);
        Assert.NotNull(e);
        Assert.Equal(3881.65m, e!.Value.Amount);
        Assert.Equal("LKR", e.Value.Currency);
    }

    [Fact]
    public void Amazon_and_payhere_and_dragon_and_aliexpress_confirmation_parse()
    {
        var az = BillParser.TryParse("auto-confirm@amazon.com", "Your Amazon.com order of \"Amazon eGift Card\"", "Order Total: USD 10.00");
        Assert.Equal("Online Shopping", az!.Value.Profile.Category);
        Assert.Equal("USD", az.Value.Currency); Assert.Equal(10.00m, az.Value.Amount);

        var vi = BillParser.TryParse("receipts@mail.payhere.lk", "Viana Cosmetics Payment Receipt #320044586599", "Subtotal LKR 8,900.00 Total LKR 8,900.00");
        Assert.Equal("Health and Wellness", vi!.Value.Profile.Category); Assert.Equal(8900.00m, vi.Value.Amount);

        var cd = BillParser.TryParse("orders@chinesedragoncafe.com", "Order #21004 confirmed", "Subtotal Rs 6,560.00 Total Rs 6,560.00");
        Assert.Equal("Food", cd!.Value.Profile.Category); Assert.Equal(6560.00m, cd.Value.Amount);

        var pc = BillParser.TryParse("receipts@mail.payhere.lk", "CHINESE DRAGON CAFE (PVT) LTD Payment Receipt #32004444", "Subtotal LKR 6,560.00 Total LKR 6,560.00");
        Assert.Equal("Food", pc!.Value.Profile.Category); Assert.Equal("PayHere Chinese Dragon", pc.Value.Profile.Name);

        var ax = BillParser.TryParse("transaction@notice.aliexpress.com", "Order 8172809454035896: order confirmation", "Order total US $1.42 details");
        Assert.Equal("Online Shopping", ax!.Value.Profile.Category);
        Assert.Equal("USD", ax.Value.Currency); Assert.Equal(1.42m, ax.Value.Amount);
    }

    [Fact]
    public void GooglePlay_imports_free_zero_items_but_paid_items_still_prefer_positive_total()
    {
        // Free item: 0.00 is recorded, not skipped.
        var free = BillParser.TryParse("googleplay-noreply@google.com", "Your Google Play Order Receipt from Jan 1, 2026",
            "Some Free App Total : රු. 0.00");
        Assert.NotNull(free);
        Assert.Equal(0m, free!.Value.Amount);

        // Paid item with a 0.00 line before the real total → still takes the positive one.
        var paid = BillParser.TryParse("googleplay-noreply@google.com", "Your Google Play Order Receipt from Jan 2, 2026",
            "Tax Total : රු. 0.00 Subscription Total : රු. 5,750.00");
        Assert.Equal(5750.00m, paid!.Value.Amount);

        // A non-AllowZero profile with only a 0.00 total still returns null.
        var az = BillParser.TryParse("transaction@notice.aliexpress.com", "Order 1: order confirmed", "Order total US $0.00");
        Assert.Null(az);
    }

    [Fact]
    public void Batch_lassana_anim8_feelo_adidas_glow_phoenix_bbc()
    {
        Assert.Equal(("Gifts",7240.00m), Cat(BillParser.TryParse("orders@lassanaflora.com","Lassana.com - Order Confirmation - 23-01","Sub Total LKR 1 Grand Total LKR 7,240.00")));
        Assert.Equal(("Home",3500.00m), Cat(BillParser.TryParse("chethana@anim8.lk","Receipt for Online Payment | Anim8 | #30404","Rs. 3500.00 for order reference #30404")));
        Assert.Equal(("Food",999.00m), Cat(BillParser.TryParse("store+73659515154@t.shopifyemail.com","Order #11549 confirmed","Total 999.00 LKR Subtotal Rs. 999.00")));
        Assert.Equal(("Clothing",25950m), Cat(BillParser.TryParse("do-not-reply@global-e.com","Order confirmed - adidas by Global-e","Subtotal SL Rs25950 Free Total SL Rs25950")));
        Assert.Equal(("Health and Wellness",5650.00m), Cat(BillParser.TryParse("contact@glowbnb.com","Your Glow Body and Beauty order has been received","Subtotal: Rs 5,400.00 Total: Rs 5,650.00")));
        Assert.Equal(("Home",7540.00m), Cat(BillParser.TryParse("onlinesales@phoenix.lk","Your Phoenix Online Shop order has been received","Total: රු 7,540.00 Billing")));
        Assert.Equal(("Online Shopping",1.99m), Cat(BillParser.TryParse("bbcshop@bbc.com","Thank you for your BBC Shop order 3607799","Subtotal: £1.99 Order Total: £1.99")));
    }

    [Fact]
    public void Grab_detects_currency_namecheap_and_daraz_orders_fixed()
    {
        var g = BillParser.TryParse("no-reply@grab.com","Your Grab E-Receipt","Fare RM 22.00 Total Paid RM 22.00");
        Assert.Equal(22.00m, g!.Value.Amount); Assert.Equal("MYR", g.Value.Currency); Assert.Equal("Transport", g.Value.Profile.Category);

        var n = BillParser.TryParse("support@namecheap.com","Namecheap Order Summary (Order# 200087354);","Initial Charge : $14.98 Final Cost : $14.98");
        Assert.Equal(14.98m, n!.Value.Amount); Assert.Equal("Website", n.Value.Profile.Category);

        var d = BillParser.TryParse("orders@orders.daraz.lk","Your Order has been Confirmed (#201912072210771)","Subtotal: Rs. 989 Total: Rs. 1352");
        Assert.Equal(1352m, d!.Value.Amount); Assert.Equal("Online Shopping", d.Value.Profile.Category);
    }

    [Fact]
    public void Singer_epic_and_payhere_feelo_parse()
    {
        // Singer: "Total [Rs .] :" label, discounts + shipping already applied
        var s = BillParser.TryParse("websales@singersl.com", "Order Placed (Auto Email)",
            "13,950 - Bank Discount - 1,395 Shipping : 700 Total [Rs .] : 11,860.00");
        Assert.Equal(11860.00m, s!.Value.Amount); Assert.Equal("Home", s.Value.Profile.Category);

        // Epic: a fully-discounted giveaway totals $0.00 and must still import (AllowZero)
        var e = BillParser.TryParse("help@acct.epicgames.com", "Your Epic Games Receipt",
            "Price: $59.99 USD Sale Discount - $59.99 USD TOTAL: $0.00 USD");
        Assert.Equal(0m, e!.Value.Amount); Assert.Equal("USD", e.Value.Currency); Assert.Equal("Games", e.Value.Profile.Category);

        var f = BillParser.TryParse("receipts@mail.payhere.lk", "Feelo Payment Receipt #320041656361",
            "Subtotal LKR 999.00 Total LKR 999.00 Payment LKR 999.00");
        Assert.Equal(999.00m, f!.Value.Amount); Assert.Equal("Food", f.Value.Profile.Category);
    }

    [Fact]
    public void Anthropic_is_exempt_from_dedup_so_parallel_subscriptions_both_import()
    {
        var r = BillParser.TryParse("invoice+statements@mail.anthropic.com",
            "Your receipt from Anthropic, PBC #2950-8105-1050",
            "Claude Pro Qty 1 $20.00 Total $20.00 Amount paid $20.00");
        Assert.Equal(20.00m, r!.Value.Amount);
        Assert.Equal("USD", r.Value.Currency);
        Assert.True(r.Value.Profile.NoDedup);   // two $20 receipts on one day are two subscriptions, not a double-send
    }

    [Fact]
    public void Adidas_refund_is_flagged_and_does_not_shadow_the_order_profile()
    {
        const string body = "Products refund SLRs5550 Total SLRs5550";
        var r = BillParser.TryParse("do-not-reply@global-e.com", "Refund Notification - adidas - order number ALK01712422", body);
        Assert.Equal("Adidas Refund (Global-e)", r!.Value.Profile.Name);
        Assert.Equal(5550m, r.Value.Amount);
        Assert.True(r.Value.Profile.Refund);

        // the order confirmation from the same sender still lands on the expense profile
        var o = BillParser.TryParse("do-not-reply@global-e.com", "Order confirmed - adidas by Global-e", "Total SL Rs25950");
        Assert.Equal("Adidas (Global-e)", o!.Value.Profile.Name);
        Assert.False(o.Value.Profile.Refund);
    }

    [Theory]
    [InlineData("MOBILE NUMBER: 769014481", "Phone")]              // 7… = mobile line
    [InlineData("MOBILE NUMBER : 114103678", "Home Broadband")]    // 1… = broadband line
    [InlineData("769014481 Rs. 1229.71 Pay on or before", "Phone")]        // "click VIEW BILL" body, no PDF
    [InlineData("114103678 Rs. 3195.80 Pay on or before", "Home Broadband")]
    [InlineData("MOBILE NUMBER: 942112345", "Dialog")]             // anything else falls back
    [InlineData("no number here at all", "Dialog")]
    public void Dialog_category_comes_from_the_connection_number(string text, string expected) =>
        Assert.Equal(expected, BillProfiles.DialogCategory(text));

    [Fact]
    public void Dialog_one_profile_matches_every_subject_variant_and_both_amount_shapes()
    {
        foreach (var subject in new[]
        {
            "Dialog Mobile E-Bill for the month of Jul-2026-28577056",
            "Dialog Fixed_Solutions E-Bill for the month of Jun-2026 - 70073153",
            "E-Bill for the month of Jan-2026",
        })
        {
            var p = BillParser.Match("ebill@dialog.lk", subject);
            Assert.Equal("Dialog", p?.Name);
        }

        var pdf = BillParser.TryParse("ebill@dialog.lk", "Dialog Mobile E-Bill for the month of Jul-2026-28577056",
            "MOBILE NUMBER: 769014481 Total Charges for Bill Period 1,229.71");
        Assert.Equal(1229.71m, pdf!.Value.Amount);

        // 2026 Quadient template: field renamed to "Total Charges for Bill", and "Total Due"
        // (previous balance + payments folded in) sits right after it and must not win.
        var quadient = BillParser.TryParse("ebill@dialog.lk", "Dialog Mobile E-Bill for the month of Jul-2026-28577056",
            "MOBILE NUMBER: 769014481 Previous Due Amount 37.17 Payments 2,000.00 " +
            "Total Charges for Bill 4,639.68 Total Due 2,676.85 " +
            "Total Charges for 769014481 4,639.68 Total Charges for the Bill Period 4,639.68");
        Assert.Equal(4639.68m, quadient!.Value.Amount);
        Assert.Equal("Phone", quadient.Value.Profile.CategoryFrom!("MOBILE NUMBER : 769014481"));

        // body-only "click VIEW BILL" mail: amount sits right after the connection number
        var body = BillParser.TryParse("ebill@dialog.lk", "Dialog Fixed_Solutions E-Bill for the month of Jun-2026 - 70073153",
            "Please click on 'VIEW BILL' to see your statement. 114103678 Rs. 3195.80 Pay on or before 05.07.2026");
        Assert.Equal(3195.80m, body!.Value.Amount);
    }

    private static (string,decimal)? Cat((BillProfile Profile, decimal Amount, string Currency)? r) =>
        r is { } v ? (v.Profile.Category, v.Amount) : null;

    [Fact]
    public void Unrecognized_subject_returns_null()
    {
        var r = BillParser.TryParse("support@pickme.lk", "PickMe | Promo of the week", TripBody);
        Assert.Null(r);
    }

    [Fact]
    public void Wrong_sender_returns_null()
    {
        var r = BillParser.TryParse("noreply@uber.com", "PickMe | Email Receipt for Trip ID 1", TripBody);
        Assert.Null(r);
    }
}
