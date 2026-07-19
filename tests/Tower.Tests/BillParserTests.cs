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
    public void Dialog_fixed_uses_amount_before_due_date_and_ignores_mobile()
    {
        var body = "e-bill statement for the month of August 2024. 114103678 Rs. 3,053.77 Pay on or before 04.09.2024";
        var r = BillParser.TryParse("ebill@dialog.lk", "Dialog Fixed_Solutions E-Bill for the month of Aug-2024 - 70073153", body);
        Assert.NotNull(r);
        Assert.Equal("Home Broadband", r!.Value.Profile.Category);
        Assert.Equal(3053.77m, r.Value.Amount);

        // The "Dialog Mobile E-Bill" body would match the Mobile profile, not Fixed.
        var asFixed = BillParser.TryParse("ebill@dialog.lk", "Dialog Mobile E-Bill for the month of Apr-2025 - 28577056", body);
        Assert.Equal("Dialog Mobile", asFixed!.Value.Profile.Name);
    }

    [Fact]
    public void Dialog_mobile_maps_to_phone_and_skips_credit_months()
    {
        var due = "769014481 Rs. 1,229.71 Pay on or before 05.05.2025";
        var r = BillParser.TryParse("ebill@dialog.lk", "Dialog Mobile E-Bill for the month of Apr-2025 - 28577056", due);
        Assert.NotNull(r);
        Assert.Equal("Phone", r!.Value.Profile.Category);
        Assert.Equal(1229.71m, r.Value.Amount);

        // A credit-balance month (negative) is skipped, not imported as an expense.
        var credit = "769014481 Rs. -867.36 Pay on or before 05.05.2025";
        Assert.Null(BillParser.TryParse("ebill@dialog.lk", "Dialog Mobile E-Bill for the month of Apr-2025 - 28577056", credit));
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
