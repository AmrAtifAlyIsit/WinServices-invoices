using MongoDB.Bson;
using MongoDB.Driver;

namespace EthraiOrderFixService.Services;

public class OrderService
{
    private readonly IMongoCollection<BsonDocument> _orderCollection;

    public OrderService(IMongoCollection<BsonDocument> orderCollection)
    {
        _orderCollection = orderCollection;
    }

    public async Task<List<BsonDocument>> GetOrdersAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        var filterBuilder = Builders<BsonDocument>.Filter;
        var filter = filterBuilder.Gte("Created", startDate) & 
                     filterBuilder.Lte("Created", endDate) & 
                     filterBuilder.Eq("Invoice.Status", "PmtCompleted");
        var list = await _orderCollection.Find(filter).ToListAsync(cancellationToken);
        return list;
    }
}
