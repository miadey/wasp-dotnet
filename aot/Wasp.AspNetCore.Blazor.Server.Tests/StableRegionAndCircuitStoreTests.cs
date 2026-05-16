using System;
using System.Linq;
using Wasp.AspNetCore.Blazor.Server;
using Wasp.IcCdk;
using Xunit;

namespace Wasp.AspNetCore.Blazor.Server.Tests;

public sealed class StableRegionTests
{
    [Fact]
    public void Alloc_returns_offset_past_header_and_read_round_trips()
    {
        var region = new StableRegion(new InMemoryStableMemoryBackend());
        var payload = new byte[] { 1, 2, 3, 4, 5, 6 };

        ulong offset = region.Alloc(payload);

        Assert.True(offset >= StableRegion.PayloadBase);
        Assert.Equal(payload, region.Read(offset));
    }

    [Fact]
    public void Alloc_returns_distinct_offsets_and_both_read_back()
    {
        var region = new StableRegion(new InMemoryStableMemoryBackend());
        ulong o1 = region.Alloc(new byte[] { 0xAA });
        ulong o2 = region.Alloc(new byte[] { 0xBB, 0xCC });
        ulong o3 = region.Alloc(new byte[] { 0xDD, 0xEE, 0xFF });

        Assert.True(o2 > o1);
        Assert.True(o3 > o2);
        Assert.Equal(new byte[] { 0xAA }, region.Read(o1));
        Assert.Equal(new byte[] { 0xBB, 0xCC }, region.Read(o2));
        Assert.Equal(new byte[] { 0xDD, 0xEE, 0xFF }, region.Read(o3));
    }

    [Fact]
    public void Empty_alloc_round_trips()
    {
        var region = new StableRegion(new InMemoryStableMemoryBackend());
        ulong offset = region.Alloc(Array.Empty<byte>());
        Assert.Equal(Array.Empty<byte>(), region.Read(offset));
    }

    [Fact]
    public void Large_alloc_crosses_page_boundary_and_round_trips()
    {
        // 64 KiB page. Allocating 128 KiB forces the backend to grow.
        var region = new StableRegion(new InMemoryStableMemoryBackend());
        var big = new byte[128 * 1024];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i & 0xFF);

        ulong offset = region.Alloc(big);
        Assert.Equal(big, region.Read(offset));
    }

    [Fact]
    public void UsedBytes_reflects_high_watermark()
    {
        var region = new StableRegion(new InMemoryStableMemoryBackend());
        ulong before = region.UsedBytes;
        region.Alloc(new byte[100]);
        ulong after = region.UsedBytes;
        Assert.Equal(before + 8 + 100, after);
    }

    [Fact]
    public void Read_below_PayloadBase_throws()
    {
        var region = new StableRegion(new InMemoryStableMemoryBackend());
        Assert.Throws<ArgumentOutOfRangeException>(() => region.Read(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => region.Read(StableRegion.PayloadBase - 1));
    }

    [Fact]
    public void Independent_regions_with_same_backend_share_state()
    {
        // Two StableRegion instances over one backend should see the same
        // allocations — simulating canister rebooting after upgrade.
        var backend = new InMemoryStableMemoryBackend();
        var r1 = new StableRegion(backend);
        ulong o = r1.Alloc(new byte[] { 7, 7, 7 });

        var r2 = new StableRegion(backend);
        Assert.Equal(new byte[] { 7, 7, 7 }, r2.Read(o));
    }
}

public sealed class CircuitStoreTests
{
    private static byte[] Principal(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Save_then_Restore_returns_exact_bytes()
    {
        var store = new CircuitStore(new StableRegion(new InMemoryStableMemoryBackend()));
        var p = Principal("rrkah-test");
        var state = new byte[] { 1, 2, 3, 4 };

        store.Save(p, state);

        Assert.Equal(state, store.Restore(p));
    }

    [Fact]
    public void Restore_unknown_principal_returns_null()
    {
        var store = new CircuitStore(new StableRegion(new InMemoryStableMemoryBackend()));
        Assert.Null(store.Restore(Principal("never-saved")));
    }

    [Fact]
    public void Save_overwrites_with_latest_snapshot_for_principal()
    {
        var store = new CircuitStore(new StableRegion(new InMemoryStableMemoryBackend()));
        var p = Principal("alice");
        store.Save(p, new byte[] { 1 });
        store.Save(p, new byte[] { 2, 2 });
        store.Save(p, new byte[] { 3, 3, 3 });

        Assert.Equal(new byte[] { 3, 3, 3 }, store.Restore(p));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Multiple_principals_kept_independent()
    {
        var store = new CircuitStore(new StableRegion(new InMemoryStableMemoryBackend()));
        store.Save(Principal("a"), new byte[] { 0xAA });
        store.Save(Principal("b"), new byte[] { 0xBB });
        store.Save(Principal("c"), new byte[] { 0xCC });

        Assert.Equal(new byte[] { 0xAA }, store.Restore(Principal("a")));
        Assert.Equal(new byte[] { 0xBB }, store.Restore(Principal("b")));
        Assert.Equal(new byte[] { 0xCC }, store.Restore(Principal("c")));
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void LoadFromStable_rehydrates_index_across_simulated_upgrade()
    {
        // Simulate: original canister runs, snapshots state, then upgrades
        // (same stable backend, fresh CircuitStore + StableRegion).
        var backend = new InMemoryStableMemoryBackend();

        ulong savedIndexOffset;
        {
            var pre = new CircuitStore(new StableRegion(backend));
            pre.Save(Principal("alice"), new byte[] { 1, 1 });
            pre.Save(Principal("bob"), new byte[] { 2, 2, 2 });
            savedIndexOffset = pre.LatestIndexOffset;
        }

        var post = new CircuitStore(new StableRegion(backend));
        int restored = post.LoadFromStable(savedIndexOffset);

        Assert.Equal(2, restored);
        Assert.Equal(new byte[] { 1, 1 }, post.Restore(Principal("alice")));
        Assert.Equal(new byte[] { 2, 2, 2 }, post.Restore(Principal("bob")));
    }

    [Fact]
    public void LoadFromStable_with_zero_offset_returns_zero_no_crash()
    {
        var store = new CircuitStore(new StableRegion(new InMemoryStableMemoryBackend()));
        int restored = store.LoadFromStable(0);
        Assert.Equal(0, restored);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void LoadFromStable_with_corrupt_magic_returns_zero()
    {
        var backend = new InMemoryStableMemoryBackend();
        var region = new StableRegion(backend);
        var bogus = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 };
        ulong off = region.Alloc(bogus);

        var store = new CircuitStore(region);
        Assert.Equal(0, store.LoadFromStable(off));
    }

    [Fact]
    public void Principal_longer_than_255_bytes_rejected()
    {
        var store = new CircuitStore(new StableRegion(new InMemoryStableMemoryBackend()));
        var bad = Enumerable.Repeat((byte)0x42, 256).ToArray();
        Assert.Throws<ArgumentException>(() => store.Save(bad, new byte[] { 1 }));
    }

    [Fact]
    public void Snapshot_persists_across_upgrade_with_three_principals_in_order()
    {
        var backend = new InMemoryStableMemoryBackend();
        ulong indexOffset;
        {
            var s = new CircuitStore(new StableRegion(backend));
            s.Save(Principal("x"), new byte[] { 9 });
            s.Save(Principal("y"), new byte[] { 8, 8 });
            s.Save(Principal("z"), new byte[] { 7, 7, 7 });
            indexOffset = s.LatestIndexOffset;
        }

        var s2 = new CircuitStore(new StableRegion(backend));
        s2.LoadFromStable(indexOffset);

        Assert.Equal(new byte[] { 9 }, s2.Restore(Principal("x")));
        Assert.Equal(new byte[] { 8, 8 }, s2.Restore(Principal("y")));
        Assert.Equal(new byte[] { 7, 7, 7 }, s2.Restore(Principal("z")));
    }
}
