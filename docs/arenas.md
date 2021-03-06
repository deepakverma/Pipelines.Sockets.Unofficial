# Arena Allocation

## What is an arena allocator?

An [arena allocator is a mechanism for allocating memory](https://en.wikipedia.org/wiki/Region-based_memory_management) as successive chunks from one or more segments owned by the allocator. In particular, it is useful when allocating lots of array-like blocks during batch processing, and at the end of the batch **nothing survives**:

- allocation becomes very cheap because we're just taking successive slivers from large slabs
- collection becomes *free*: since nothing outlives the batch, we just set our eyes back to the start

For the purposes of this API, I'm talking about arena allocators specifically in the context of allocating array-like structures (think `T[]`, `ArrayPool<T>`, etc).

You can think of it as "stadium seating":

- "hi, I need 92 seats" - "sure, take all of stand A (50 seats), and the first 42 seats from stand B"
- ...
- "hi, I need 14 seats" - "sure, take the next 14 seats in stand C"
- "hi, I need 8 seats" - "hmm, there's only 3 left in stand C; take 3 from stand C and the first five 5 from stand D"
- ...
- "ok, we're about to start a new event" - "OK, everyone's seat is now invalid... just don't use them, thanks; I'm going to start handing out seats at the start of stand A"

By comparison:

- regular array allocation (`new T[size]`) uses the regular allocator, and creates objects (arrays) that the runtime expects *could* outlive the batch (it has no concept of a batch); this means that the garbage collector (GC) needs to track all of those arrays and check when they become unreachable, before reclaiming the underlying memory
- `ArrayPool<T>` allocation allows you to avoid some of this constant churn by claiming arrays from a pool, and returning them to the pool when complete; this avoids some of the GC overhead, but the pool itself has not inconsiderable overhead; additionally:
  - the arrays you get can be scattered *all over memory* (reducing memory locality performance)
  - the arrays you get can be *oversized*, so you need to track the *actual* length in addition to the array
  - you need to add code to manually *return* all of the rented arrays to the pool at the end of the batch - which gets expensive for complex graphs

Arena allocation avoids the downsides of simple or pooled array allocation:

- allocations become very cheap and simple
- allocations typically come from neighbouring memory areas, maximizing memory locality
- collection becomes very cheap
- both of the extreme allocation scenarios (high volumes of very small allocations, and low volumes of very large allocations) become **much** less painful
- but: we might need to consider "non-contiguous memory" (e.g. "take 3 from stand C and the first five 5 from stand D")

The last point sounds like an inconvenience, but if you're touching "pipelines", you're probably already familiar with `ReadOnlySequence<T>`, which is **exactly** this scenario, expressed in read-only terms; so we can use this familiar concept, but in a read/write model.

## Introducing `Arena` and `Arena<T>`

To help solve this, we add `Arena<T>`, an arena allocator for blocks of memory of type `T` - broadly comparable to `T[]`/`ArraySegment<T>`/`Memory<T>`. In many cases, however, we might
have multiple types involved in a batch; to help with this, the non-generic `Arena` extends this further by providing efficient access to *multiple* types.

Usage of `Arena<T>` is simple:

``` c#
using (var arena = new Arena<SomeType>()) // note: options available on .ctor
{
    while (haveWorkToDo) // typically you'll be processing multiple batches
    {
        // ... blah blah lots of work ...

        foreach (var row in something)
        {
            // get a chunk of memory
            var block = arena.Allocate(42);
            // ...
            var anotherBlock = arena.Allocate(itemCount);
            // ...
        }
        
        
        // ... more work ...

        // END OF THIS BATCH - we're going to invalidate all the
        // allocations that we constructed during this batch, and
        // start again
        arena.Reset();
    } 
}
```

In the above, `block` and `anotherBlock` are `Sequence<SomeType>` values; these are the write-friendly cousins of `ReadOnlySequence<SomeType>` with all the features you'd expect - `.Length`, `.Slice(...)`, access to the internal data, etc. Importantly, an `Sequence<T>` is a `readonly struct` value-type (meaning: zero allocations). As with `ReadOnlySequence<T>`, using them is complicated slightly by the fact that it *may* (sometimes) involve non-contiguous memory, so just like with `ReadOnlySequence<T>`, *typically* you'd want to check `.IsSingleSegment` and access `.FirstSpan` (`Span<T>`) or `.FirstSegment` (`Memory<T>`) for an optimized simple fast path, falling back to multi-segment processing otherwise.

If we want to use `Arena` to allocate sequences of multiple types:

``` c#
using (var arena = new Arena()) // note: options available on .ctor
{
    while (haveWorkToDo) // typically you'll be processing multiple batches
    {
        // ... blah blah lots of work ...

        foreach (var row in something)
        {
            // get a chunk of memory
            var block = arena.Allocate<long>(42);
            // ...
            var anotherBlock = arena.Allocate<Foo>(itemCount);
            // ...
        }
        
        
        // ... more work ...

        // END OF THIS BATCH - we're going to invalidate all the
        // allocations that we constructed during this batch, and
        // start again
        arena.Reset();
    } 
}
```

Note that we now use the generic `Allocate<T>` method, supplying the target type at the call-site. The returned sequence types remain the same, but internally the arena can optimize memory usage by
allocating all types that satisfy `T : unmanaged` (i.e. `struct` types that don't include any reference-type fields) by allocating them all from the same raw memory.

## How to work with the values in a sequence

`Sequence<T>` works very similarly to `ReadOnlySequence<T>`, with some tweaks. Firstly, unlike `ReadOnlySequence<T>`, `Sequence<T>` exposes an *indexer*; this indexer
is of type `ref T`, so you aren't copying the value - you're accessing it *in situ* (unless you choose to de-reference it). You can also assign directly to the reference, or mutate the
value via the reference. If the mention of `ref T` references sounds scary: it really isn't; ultimately this is identical to what the array (`T[]`) and span (`Span<T>`)
indexers already return; in most cases you simply don't need to stress about it.

You can also iterate over the individual segments (`Memory<T>`) if needed, or the segments as spans (`Span<T>`)

``` c#
foreach (Memory<T> segment in block.Segments) { ... }
foreach (Span<T> span in block.Spans) { ... }
```

Note that because the segments and spans here are writable rather than read-only, assigning values is as simple as writing to the span indexers.

Alternatively, you iterate over the *values*:

``` c#
foreach (T value in block) { ... }
```

and if you're using C# 8.0 or above, you can iterate over the items *as references* - avoiding a stack copy:

``` c#
foreach (ref T value in block) { ... }
foreach (ref readonly T value in block) { ... }
```

And just like with the indexer, the `foreach (ref T value in block)` syntax allows you to assign *to the l-value* (`value`), or mutate the value via the reference.
This usage is particularly useful when `T` is a large `struct`, as it avoids copying non-trivial types on the stack. It also works particularly well when passing
the values to methods that take `in T` or `ref T` parameters.

---

The most common pattern - and the most efficient - is to iterate over a span via the indexer, so a *very* common usage might be:

``` c#
static decimal SumOrderValue(Sequence<Order> orders)
{
    static decimal Sum(Span<Order> span)
    {
        decimal total = 0;
        for(int i = 0 ; i < span.Length ; i++)
            total += span[i].NetValue;
        return total;
    }

    if (orders.IsSingleSegment) return Sum(orders.FirstSpan);
    decimal total = 0;
    foreach (var span in orders.Spans)
        total += Sum(span);
    }
    return total;
}
```

Or we could write it *more conviently* (but less efficiently) as:

``` c#
static decimal SumOrderValue(Sequence<Order> orders)
{
    decimal total = 0;
    foreach (var order in orders)
        total += order.NetValue;
    return total;    
}
```

However, we can *also* make use of the value iterator directly in some interesing ways - more than just `foreach`. In particular:

- `.MoveNext()` behaves as you would expect, returning `false` at the end of the data
- `.Current` on the iterator return a reference to the value (`ref return`), allowing both read and write
- `.GetNext()` **asserts** that there is more data, and then returns a reference (`ref return`) to the next element (essentially combining `MoveNext()` and `Current` into a single step)


The `GetNext()` option is particularly useful for migrating existing code; consider:

``` c#
int[] values = new int[itemCount];
int offset = 0;
foreach (var item in ...)
    values[offset++] = item.Something();
return values;
```

We can migrate this conveniently to:

``` c#
var values = arena.Allocate(itemCount);
var iter = values.GetEnumerator();
foreach (var item in ...)
    iter.GetNext() = item.Something();
return values;
```

(of course we *could* also choose to check `values.IsSingleSegment` and optimize for the single-segment variant, which is usually **very common**, but ... we don't *have* to).

## Conversion to `ReadOnlySequence<T>`

Because the `Sequence<T>` concept is so close to `ReadOnlySequence<T>`, you may want to get the read-only version of the memory; fortunatly, this is simple:

``` c#
Sequence<T> block = ...
ReadOnlySequence<T> readOnly = block.AsReadOnly();
```

(or via the `implicit` conversion operator)

You can even *convert back again* - but **only** from sequences that were obtained from an `Sequence<T>` in the first place:

``` c#
ReadOnlySequence<T> readOnly = ...
if (Sequence<T>.TryGetAllocation(readOnly, out var block))
{
    ...
}
```

(or via the `explicit` conversion operator, which will `throw` for invalid sequences)



## Recursive structs

A common pattern in data parsing is the [DOM](https://en.wikipedia.org/wiki/Document_Object_Model), and in a low-allocation parser you might want to create a `struct`-based DOM, i.e.

``` c#
readonly struct Node {
    // not shown: some other state
    public Sequence<Node> Children { get; }
    public Node(/* other state */, Sequence<Node> children)
    {
        // not shown: assign other state
        Children = children;
    }
}
```

The above is trivially possible with arrays (`T[]`), but the above declaration is actually invalid; the CLR assumes that `Sequence<T>` could feasibly contain a field of type `T` (now, or in the future), and if it did, a `struct` would indirectly contain itself. This makes it impossible to determine the size of the `struct`, and the CLR refuses to accept the type. It works with naked arrays because the runtime knows that an array is a regular reference, but the above would also fail for `Memory<Node>`, `ReadOnlySequence<Node>`, `ArraySegment<Node>` etc.

Because this type of scenario is desirable, we add an additional concept to help us with this use-case: *untyped allocations*. Actually, they aren't really untyped - it is all just a trick to make the runtime happy, but - the following works perfectly and remains zero-allocation:

``` c#
readonly struct Node {
    // not shown: some other state
    private readonly Allocation _children; // note no <T> here
    public Sequence<Node> Children => _children.Cast<Node>();
    public Node(/* other state */, Sequence<Node> children)
    {
        // not shown: assign other state
        _children = children.Untyped();
    }
}
```

(conversion operators are provided for convenience, too - `implicit` for the `Untyped()` step and `explicit` for the `Cast<T>()` step)

## References (flyweights)

Sometimes, again when dealing with large `struct T`, it would be nice if we could embed a value without paying the cost of embedding a large field into another type, or dealing
with lots of stack copies. In the scope of a local method, we can do this using "`ref` locals", i.e.

``` c#
ref Foo x = ref span[42];
// x isn't a Foo *instance* - it is a *reference* to the instance
```

Unfortunately, `ref` fields are not permitted, even in `ref struct` types. As a compromise, arenas provides a similar concept to a `ref` field: a [flyweight](https://en.wikipedia.org/wiki/Flyweight_pattern), specifically
via `Reference<T>`. Again, this is another value type, so there is no allocation cost - and a `Reference<T>` value knows where to look for the *actual* value inside the arena.

By using `ref return`, the flyweight - which itself is suitably small - can be used as a field to provide access to a reference:

``` c#
Reference<Foo> myRef = arena.Allocate(); // note no length supplied
```

From here, `myRef.Value` is a *reference* to the data in the arena, so it can be read, written, passed as a reference, etc very cheaply. There is also an `implicit` conversion
operator from `Reference<T>` to `T`.


## Configuration

A number of configuration options are provided in the `Arena<T>` constructor:

- `allocator` - the underlying source of data; in terms of our "stadium" example, this is used whenever the arena finds that it doesn't have enough seats, to construct a new stand
  - by default, an allocator based on `ArrayPool<T>.Shared` is used - taking arrays when needed, and returning them when not
  - you can create allocators based on custom `ArrayPool<T>` instances
  - if you prefer, `UnmanagedAllocator<T>.Shared` can be used for any `T : unmanaged`, which uses `Marshal.AllocHGlobal` and `Marshal.FreeHGlobal`
  - or you can provide your own custom allocator if you want to do something more exotic
- `options` - a set of flags that impact overall behaviour:
  - `ArenaFlags.ClearAtReset` ensures that memory is wiped (to zero) before handing it to consumers via `Allocate()`; for performance, rather than wipe per allocation, the entire allocated region is wiped when `Reset()` is called, or whenever a new block is allocated
  - `ArenaFlags.ClearAtDispose` ensures that memory is wiped (to zero) before the underlying allocator returns it to wherever it came from; this prevents your data from become accessible to arbitrary consumers (for example via `ArrayPool<T>` when using the default configuration) and is *broadly* comparable to the `ArrayPool<T>.Return(array, clearArray: true)` option; note that this option can not prevent direct memory access from seeing your data while in use (nothing can)
- `blockSize` - the amount (in elements of `T`) to request from the underlying allocator when a new block is required; note that the allocator uses this for guidance only and could allocate different amounts. The default option is to allocate blocks of 128KiB - this ensures low fragmentation and (for the default array-based allocator) means that the backing arrays are on the Large Object Heap; a large block size also means that virtually all allocations are single-segment rather than multi-segment
- `retentionPolicy` - provides a mechanism for determining when to *release* memory at the end of each batches; it is normal for the amount of memory to fluctuate between batches, so it usually isn't desirable to release everything each time; the default policy is to use an exponential decay of 90% - meaning: each time we `Reset()`, if we had used less memory than before, we'll drop the amount to retain to 90% of what it was previously (if we used *more*, it'll jump up to the new amount immediately). This means that we don't *immediately* release untouched blocks, but if we *consistently* find ourselves using much less than we previously have, we will *eventually* release blocks. Other common policies (`Recent`, `Nothing`, `Everything`, etc) are available under `RetentionPolicy` - or you can provide your own custom retention policy as a function of the previously retained amount, and the amount used in the current batch (`Func<long,long,long>`)

## Performance

Performance is shown in the image below; a short summary would be:

- allocations are about 25 times faster than `new T[]` arrays (presumably mostly due to GC overheads), and about 10-15 times faster than `ArrayPool<T>` (presumably due to the more complex work required by an array-pool)
- read and write when using optimized `for` loops using the span indexers are, on .NET Core, idential to using similar `for` loops using arrays; on .NET Framework (which has a different `Span<T>` implementation and may lack bounds-check elision), performance is degraded to half that of raw arrays (this is endemic to `Span<T>` generally, and is not specific to `Arena<T>`)
- read when using element-based `foreach` loops is degraded to a quarter of the performance when compared to arrays; for a comparison/baseline, when using `ArraySegment<T>` (a convenient way of representing `ArrayPool<T>` leases, due to the fact that leased arrays are oversized), this is actually very good, with `ArraySegment<T>` being *twice as slow* as the `Sequence<T>` implementation of simple `foreach`

So: on .NET Core in particular, there is *zero loss* (especially if you're using optimized span-based access), and **lots of win** in allocation. On .NET Framework, there is *some loss* (an *extremely* fast number becomes a *very* fast number when using span-based access), and still **lots of win** in allocation. Use of `foreach` is *perfecty acceptable* (but not as fast as span-based access) - especially for non-critical code-paths.

![performance](performance.png)

## Other questions

### What about thread-safety?

An `Arena<T>` is not thread-safe; normally, it is assumed that an individual batch will only be processed by a single thread, so we don't attempt to make it thread-safe. If you have a batch-processing scenario where you *can* process the data in parallel: that's fine, but you will need to add some kind of synchrnoization (usually via `lock`) around the calls to `Allocate()` and `Reset()`. Note that two *separate* concurrent batches should not usually use the same `Arena<T>`, unless you are happy that `Reset()` applies to both of them.

### What if I keep hold of a sequence or reference?

If your code chooses to leak an `Sequence<T>` outside of a batch (more specifically: past a `Reset()` call, or past the arena's final `Dispose()` call), then: **that's on you**. What happens next is undefined, but commonly:

- you could be stomping over data that is still in the arena; it may or may not have been wiped (depending on the configuration), and may or may not have been handed to new allocations in later batches
- the data may have been released from the arena back to the allocator; if you're using the default `ArrayPool<T>` allocator, then this is similar to holding onto an array after calling `ArrayPool<T>.Return(...)`; if you're using an unmanaged allocater, this could immediately give you an access violation ... or you could just end up overwriting arbitrary memory now in use for other purposes inside your application

Basically: **just don't do it**. Part of choosing to use an `Arena<T>` is making the determination that **you know the lifetime of your data**, and are therefore choosing not to do silly things. The risks here are mostly comparable to identical concerns when using `ArrayPool<T>`, so this is a perfectly reasonable and acceptable compromise.

### How can I play with it?

It is available [on nuget from 1.1.* onwards](https://www.nuget.org/packages/Pipelines.Sockets.Unofficial/). Have fun!

### Could I create something like `List<T>` on top of it?

Absolutely! It *probably* makes sense for any such type to be a `class` rather than a `struct`, though, since a lot of list access is done via interfaces, which *usually* involves boxing (since not many people write APIs that take things like `<TList, ITtem> where TList : IList<TItem>`, which is what you'd need for "constrained" access without boxing).

Considerations:

- indexer access might be expensive, and the current `Sequence<T>.Enumerator` is a `ref struct`, meaning you can't store it as a field (in the hope that most indexer access is actually incremental); we could perhaps add some API to help with this, perhaps based on `SequencePosition`?
- you can't *expand* (enlarge) an `Sequence<T>` (but then... you can't resize a `T[]` *at all*); you can, however, *shrink* an `Sequence<T>` - by using `Slice()` (which returns a sub-range of an existing allocation)
- most other common API surfaces should be easy to add, though
