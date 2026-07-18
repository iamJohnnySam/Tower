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
