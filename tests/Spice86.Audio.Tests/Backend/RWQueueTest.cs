namespace Spice86.Audio.Tests.Backend;

using FluentAssertions;

using Spice86.Audio.Backend;

using Xunit;

/// <summary>
/// Tests for <see cref="RWQueue{T}"/>, ported from DOSBox Staging's rwqueue tests.
/// </summary>
public class RWQueueTest {
    /// <summary>
    /// Reproduces the IndexOutOfRangeException that occurs when:
    /// 1. A queue is created with some capacity.
    /// 2. Items are enqueued to fill the queue.
    /// 3. Resize is called (which sets _tail = itemsToCopy, potentially equal to _buffer.Length).
    /// 4. An item is dequeued (making room).
    /// 5. NonblockingEnqueue is called — _tail is now out-of-bounds.
    /// </summary>
    [Fact]
    public void NonblockingEnqueue_AfterResizeOfFullQueue_ShouldNotThrow() {
        // Arrange — fill a queue, then resize down so _tail == _capacity == _buffer.Length
        RWQueue<int> queue = new RWQueue<int>(6);
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        queue.Enqueue(4);

        // Resize down to count — _tail becomes itemsToCopy == newCapacity
        queue.Resize(4);
        queue.Size.Should().Be(4);

        // Dequeue one to make room
        int item = queue.Dequeue(out bool success);
        success.Should().BeTrue();
        item.Should().Be(1);
        queue.Size.Should().Be(3);

        // Act — this should not throw IndexOutOfRangeException
        bool enqueued = queue.NonblockingEnqueue(5);

        // Assert
        enqueued.Should().BeTrue();
        queue.Size.Should().Be(4);
    }

    /// <summary>
    /// Same bug reproduced via blocking Enqueue path.
    /// </summary>
    [Fact]
    public void Enqueue_AfterResizeOfFullQueue_ShouldNotThrow() {
        // Arrange
        RWQueue<int> queue = new RWQueue<int>(4);
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        queue.Enqueue(4);

        // Resize to a different capacity that still copies all items
        queue.Resize(6);
        // Now resize back down so that the copied items fill the new capacity
        queue.Resize(4);

        // Dequeue to make room
        queue.Dequeue(out bool success);
        success.Should().BeTrue();

        // Act — this should not throw IndexOutOfRangeException
        bool enqueued = queue.Enqueue(10);

        // Assert
        enqueued.Should().BeTrue();
        queue.Size.Should().Be(4);
    }

    /// <summary>
    /// Verifies that BulkEnqueue after resize of a full queue works correctly.
    /// </summary>
    [Fact]
    public void BulkEnqueue_AfterResizeOfFullQueue_ShouldNotThrow() {
        // Arrange — fill, then resize down so _tail == _capacity
        RWQueue<int> queue = new RWQueue<int>(6);
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        queue.Enqueue(4);

        queue.Resize(4);

        // Dequeue two items to make room
        queue.Dequeue(out _);
        queue.Dequeue(out _);

        // Act
        Span<int> data = stackalloc int[] { 10, 20 };
        int enqueued = queue.BulkEnqueue(data);

        // Assert
        enqueued.Should().Be(2);
        queue.Size.Should().Be(4);
    }

    /// <summary>
    /// Verifies that resizing to a larger capacity while full does not corrupt tail.
    /// </summary>
    [Fact]
    public void Resize_LargerCapacity_WhenFull_PreservesTailCorrectly() {
        // Arrange
        RWQueue<int> queue = new RWQueue<int>(2);
        queue.Enqueue(1);
        queue.Enqueue(2);

        // Act — resize to larger
        queue.Resize(4);
        queue.Size.Should().Be(2);

        // Should be able to enqueue more
        bool enqueued = queue.NonblockingEnqueue(3);
        enqueued.Should().BeTrue();

        enqueued = queue.NonblockingEnqueue(4);
        enqueued.Should().BeTrue();

        queue.Size.Should().Be(4);
        queue.IsFull.Should().BeTrue();
    }

    /// <summary>
    /// Verifies FIFO order is maintained after resize of a full queue.
    /// </summary>
    [Fact]
    public void Resize_FullQueue_MaintainsFifoOrder() {
        // Arrange
        RWQueue<int> queue = new RWQueue<int>(3);
        queue.Enqueue(10);
        queue.Enqueue(20);
        queue.Enqueue(30);

        // Resize via different capacity (triggers linearization)
        queue.Resize(6);
        queue.Resize(3);

        // Dequeue one, enqueue one
        int val = queue.Dequeue(out bool success);
        success.Should().BeTrue();
        val.Should().Be(10);

        queue.NonblockingEnqueue(40);

        // Verify order
        queue.Dequeue(out success).Should().Be(20);
        success.Should().BeTrue();
        queue.Dequeue(out success).Should().Be(30);
        success.Should().BeTrue();
        queue.Dequeue(out success).Should().Be(40);
        success.Should().BeTrue();
    }
}
