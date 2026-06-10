using MongoDB.Bson;
using MongoDB.Driver;

namespace EthraiOrderFixService.Services;

public class CouponService
{
    private readonly IMongoCollection<BsonDocument> _couponCollection;

    public CouponService(IMongoClient client)
    {
        var userDataDb = client.GetDatabase("userdatadb");
        _couponCollection = userDataDb.GetCollection<BsonDocument>("Coupon");
    }

    /// <summary>
    /// Looks up a coupon by its Name and returns Value.PercValue, or null if not found.
    /// </summary>
    public async Task<double?> GetDiscountPercByCouponNameAsync(string couponName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(couponName))
            return null;

        var filter = Builders<BsonDocument>.Filter.Eq("_id", couponName);
        var coupon = await _couponCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);

        if (coupon == null)
            return null;

        if (coupon.Contains("Value") && coupon["Value"].AsBsonDocument.Contains("PercValue"))
            return coupon["Value"].AsBsonDocument["PercValue"].ToDouble();

        return null;
    }
}
