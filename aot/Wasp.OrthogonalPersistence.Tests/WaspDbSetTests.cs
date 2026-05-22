using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wasp.OrthogonalPersistence;
using Xunit;

// Pure in-memory exercise of the WaspDbSet<T> + WaspDbContext API.
// Storage (Save/Load) requires Ic0 stable_* syscalls so isn't testable
// outside a canister; verified live in samples/TodoEf instead.
public partial class WaspDbSetTests
{
    public sealed class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
    }

    [JsonSerializable(typeof(Product))]
    [JsonSerializable(typeof(Product[]))]
    [JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, object>))]
    [JsonSerializable(typeof(object))]
    public partial class TestCtx : JsonSerializerContext { }

    private sealed class ShopContext : WaspDbContext
    {
        public WaspDbSet<Product> Products { get; }
        public ShopContext(JsonSerializerOptions json) : base(json)
        {
            Products = new WaspDbSet<Product>(this, "products");
        }
    }

    private static JsonSerializerOptions Options() => new()
    {
        TypeInfoResolver = TestCtx.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Add_increases_count()
    {
        var ctx = new ShopContext(Options());
        ctx.Products.Add(new Product { Id = 1, Name = "Apple", Price = 0.5m });
        ctx.Products.Add(new Product { Id = 2, Name = "Bread", Price = 2.0m });
        Assert.Equal(2, ctx.Products.Count);
    }

    [Fact]
    public void Remove_drops_the_item()
    {
        var ctx = new ShopContext(Options());
        var p = new Product { Id = 1, Name = "Apple" };
        ctx.Products.Add(p);
        Assert.True(ctx.Products.Remove(p));
        Assert.Equal(0, ctx.Products.Count);
    }

    [Fact]
    public void Linq_query_against_dbset_works()
    {
        var ctx = new ShopContext(Options());
        ctx.Products.Add(new Product { Id = 1, Name = "Apple", Price = 0.5m });
        ctx.Products.Add(new Product { Id = 2, Name = "Anvil", Price = 99.0m });
        ctx.Products.Add(new Product { Id = 3, Name = "Bread", Price = 2.0m });
        var cheap = ctx.Products.Where(p => p.Price < 10m).Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Apple", "Bread" }, cheap);
    }

    [Fact]
    public void Find_returns_matching_or_null()
    {
        var ctx = new ShopContext(Options());
        ctx.Products.Add(new Product { Id = 1, Name = "Apple" });
        ctx.Products.Add(new Product { Id = 2, Name = "Bread" });
        Assert.Equal("Bread", ctx.Products.Find(p => p.Id == 2)?.Name);
        Assert.Null(ctx.Products.Find(p => p.Id == 999));
    }
}
