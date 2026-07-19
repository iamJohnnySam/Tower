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

        // The "Dialog Mobile E-Bill" from the same sender must NOT match this profile.
        var mobile = BillParser.TryParse("ebill@dialog.lk", "Dialog Mobile E-Bill for the month of Apr-2025 - 28577056", body);
        Assert.Null(mobile);
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
