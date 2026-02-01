using System;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Resilience;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Resilience
{
    public class CircularBufferTests
    {
        #region Constructor Tests

        [Fact]
        public void Constructor_WithValidCapacity_CreatesBuffer()
        {
            // Arrange & Act
            var buffer = new CircularBuffer<int>(5);

            // Assert
            Assert.Equal(5, buffer.Capacity);
            Assert.Equal(0, buffer.Count);
            Assert.True(buffer.IsEmpty);
            Assert.False(buffer.IsFull);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-100)]
        public void Constructor_WithInvalidCapacity_ThrowsArgumentOutOfRangeException(int capacity)
        {
            // Arrange & Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(capacity));
        }

        #endregion

        #region Add/Count Tracking Tests

        [Fact]
        public void Add_SingleItem_IncrementsCount()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act
            buffer.Add(42);

            // Assert
            Assert.Equal(1, buffer.Count);
            Assert.False(buffer.IsEmpty);
            Assert.False(buffer.IsFull);
        }

        [Fact]
        public void Add_FillToCapacity_UpdatesIsFull()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);

            // Act
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Assert
            Assert.Equal(3, buffer.Count);
            Assert.True(buffer.IsFull);
            Assert.False(buffer.IsEmpty);
        }

        [Fact]
        public void Add_ExceedCapacity_OverwritesOldest()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            buffer.Add(4);

            // Assert
            Assert.Equal(3, buffer.Count);
            var items = buffer.ToList();
            Assert.DoesNotContain(1, items);
            Assert.Contains(2, items);
            Assert.Contains(3, items);
            Assert.Contains(4, items);
        }

        [Fact]
        public void Add_MultipleOverwrites_MaintainsCorrectCount()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);

            // Act
            for (int i = 0; i < 100; i++)
            {
                buffer.Add(i);
            }

            // Assert
            Assert.Equal(3, buffer.Count);
            var items = buffer.ToList();
            Assert.Equal(3, items.Count);
            Assert.Equal(97, items[0]);
            Assert.Equal(98, items[1]);
            Assert.Equal(99, items[2]);
        }

        #endregion

        #region Wraparound Logic Tests

        [Fact]
        public void ToList_Wraparound_MaintainsChronologicalOrder()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act - Cause wraparound
            buffer.Add(4);
            buffer.Add(5);

            // Assert
            var items = buffer.ToList();
            Assert.Equal(3, items.Count);
            Assert.Equal(3, items[0]); // Oldest
            Assert.Equal(4, items[1]);
            Assert.Equal(5, items[2]); // Newest
        }

        [Fact]
        public void ToList_CompleteWraparound_CorrectOrder()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            for (int i = 1; i <= 5; i++)
            {
                buffer.Add(i);
            }

            // Act - Wrap completely around
            buffer.Add(6);
            buffer.Add(7);
            buffer.Add(8);

            // Assert
            var items = buffer.ToList();
            Assert.Equal(5, items.Count);
            Assert.Equal(4, items[0]);
            Assert.Equal(5, items[1]);
            Assert.Equal(6, items[2]);
            Assert.Equal(7, items[3]);
            Assert.Equal(8, items[4]);
        }

        [Fact]
        public void ToList_MultipleWraparounds_PreservesOrder()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(4);

            // Act - Add many items causing multiple wraparounds
            for (int i = 0; i < 20; i++)
            {
                buffer.Add(i);
            }

            // Assert
            var items = buffer.ToList();
            Assert.Equal(4, items.Count);
            Assert.Equal(16, items[0]);
            Assert.Equal(17, items[1]);
            Assert.Equal(18, items[2]);
            Assert.Equal(19, items[3]);
        }

        [Fact]
        public void PeekNewest_AfterWraparound_ReturnsCorrectElement()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Add(4);

            // Act
            var newest = buffer.PeekNewest();

            // Assert
            Assert.Equal(4, newest);
        }

        [Fact]
        public void PeekOldest_AfterWraparound_ReturnsCorrectElement()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Add(4);

            // Act
            var oldest = buffer.PeekOldest();

            // Assert
            Assert.Equal(2, oldest);
        }

        #endregion

        #region Thread Safety Tests

        [Fact]
        public async Task Add_ConcurrentAccess_MaintainsCorrectCount()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(1000);
            var tasks = new Task[10];
            var itemsPerTask = 100;

            // Act
            for (int i = 0; i < tasks.Length; i++)
            {
                int taskIndex = i;
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < itemsPerTask; j++)
                    {
                        buffer.Add(taskIndex * itemsPerTask + j);
                    }
                });
            }
            await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(1000, buffer.Count); // Capacity is 1000
            Assert.True(buffer.IsFull);
        }

        [Fact]
        public async Task Clear_ConcurrentWithAdd_DoesNotCorruptState()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(100);

            // Act
            var addTask = Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    buffer.Add(i);
                }
            });

            var clearTask = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    buffer.Clear();
                    Task.Delay(1).Wait();
                }
            });

            await Task.WhenAll(addTask, clearTask);

            // Assert - Should be in a valid state
            var count = buffer.Count;
            Assert.True(count >= 0 && count <= buffer.Capacity);
            var items = buffer.ToList();
            Assert.True(items.Count <= buffer.Capacity);
        }

        [Fact]
        public async Task ToList_ConcurrentWithAdd_ReturnsValidList()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(50);

            // Act
            var addTask = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    buffer.Add(i);
                }
            });

            var readTasks = new Task[10];
            for (int i = 0; i < readTasks.Length; i++)
            {
                readTasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 50; j++)
                    {
                        var items = buffer.ToList();
                        Assert.True(items.Count <= buffer.Capacity);
                    }
                });
            }

            await Task.WhenAll(addTask, Task.WhenAll(readTasks));

            // Assert
            Assert.Equal(50, buffer.Count);
        }

        [Fact]
        public async Task CountWhere_ConcurrentAccess_ThreadSafe()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(100);
            for (int i = 0; i < 100; i++)
            {
                buffer.Add(i);
            }

            // Act
            var tasks = new Task[10];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 100; j++)
                    {
                        var evenCount = buffer.CountWhere(x => x % 2 == 0);
                        Assert.True(evenCount >= 0 && evenCount <= buffer.Capacity);
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(50, buffer.CountWhere(x => x % 2 == 0));
        }

        [Fact]
        public async Task PeekOperations_ConcurrentAccess_ThreadSafe()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(50);
            for (int i = 0; i < 50; i++)
            {
                buffer.Add(i);
            }

            // Act
            var tasks = new Task[10];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 100; j++)
                    {
                        buffer.TryPeekNewest(out var newest);
                        buffer.TryPeekOldest(out var oldest);
                        Assert.True(newest >= 0 && newest <= 1000);
                        Assert.True(oldest >= 0 && oldest <= 1000);
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(49, buffer.PeekNewest());
            Assert.Equal(0, buffer.PeekOldest());
        }

        #endregion

        #region Buffer Full/Empty Tests

        [Fact]
        public void IsEmpty_NewBuffer_ReturnsTrue()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act & Assert
            Assert.True(buffer.IsEmpty);
        }

        [Fact]
        public void IsEmpty_AfterAdd_ReturnsFalse()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act
            buffer.Add(1);

            // Assert
            Assert.False(buffer.IsEmpty);
        }

        [Fact]
        public void IsEmpty_AfterClear_ReturnsTrue()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);
            buffer.Add(2);

            // Act
            buffer.Clear();

            // Assert
            Assert.True(buffer.IsEmpty);
        }

        [Fact]
        public void IsFull_AtCapacity_ReturnsTrue()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act & Assert
            Assert.True(buffer.IsFull);
        }

        [Fact]
        public void IsFull_AfterClear_ReturnsFalse()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            buffer.Clear();

            // Assert
            Assert.False(buffer.IsFull);
        }

        [Fact]
        public void IsFull_WithSingleItemCapacity_WorksCorrectly()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(1);

            // Act
            buffer.Add(42);

            // Assert
            Assert.True(buffer.IsFull);
            Assert.False(buffer.IsEmpty);

            // Act - Add another item
            buffer.Add(43);

            // Assert
            Assert.True(buffer.IsFull);
            Assert.Equal(1, buffer.Count);
        }

        #endregion

        #region Capacity Management Tests

        [Fact]
        public void Capacity_RemainsConstant()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Clear();
            buffer.Add(4);

            // Assert
            Assert.Equal(5, buffer.Capacity);
        }

        [Fact]
        public void Count_NeverExceedsCapacity()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(10);

            // Act
            for (int i = 0; i < 1000; i++)
            {
                buffer.Add(i);
            }

            // Assert
            Assert.Equal(10, buffer.Count);
            Assert.True(buffer.Count <= buffer.Capacity);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(10)]
        [InlineData(100)]
        [InlineData(1000)]
        public void Capacity_DifferentSizes_AllowsCorrectCount(int capacity)
        {
            // Arrange
            var buffer = new CircularBuffer<int>(capacity);

            // Act
            for (int i = 0; i < capacity; i++)
            {
                buffer.Add(i);
            }

            // Assert
            Assert.Equal(capacity, buffer.Count);
            Assert.True(buffer.IsFull);
        }

        #endregion

        #region Count Tracking Accuracy Tests

        [Fact]
        public void Count_AfterSequentialAdd_TracksCorrectly()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act & Assert
            buffer.Add(1);
            Assert.Equal(1, buffer.Count);

            buffer.Add(2);
            Assert.Equal(2, buffer.Count);

            buffer.Add(3);
            Assert.Equal(3, buffer.Count);

            buffer.Add(4);
            Assert.Equal(4, buffer.Count);

            buffer.Add(5);
            Assert.Equal(5, buffer.Count);
        }

        [Fact]
        public void Count_AfterOverwrite_RemainsAtCapacity()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            buffer.Add(4);
            Assert.Equal(3, buffer.Count);

            buffer.Add(5);
            Assert.Equal(3, buffer.Count);

            buffer.Add(6);
            Assert.Equal(3, buffer.Count);
        }

        [Fact]
        public void Count_AfterClear_ResetsToZero()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            for (int i = 0; i < 5; i++)
            {
                buffer.Add(i);
            }

            // Act
            buffer.Clear();

            // Assert
            Assert.Equal(0, buffer.Count);
        }

        [Fact]
        public void Count_AfterPartialClearAndRefill_TracksCorrectly()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            for (int i = 0; i < 5; i++)
            {
                buffer.Add(i);
            }

            // Act
            buffer.Clear();
            buffer.Add(1);
            buffer.Add(2);

            // Assert
            Assert.Equal(2, buffer.Count);
        }

        #endregion

        #region Clear Operation Tests

        [Fact]
        public void Clear_EmptyBuffer_RemainsEmpty()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act
            buffer.Clear();

            // Assert
            Assert.Equal(0, buffer.Count);
            Assert.True(buffer.IsEmpty);
        }

        [Fact]
        public void Clear_FullBuffer_ResetsState()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            buffer.Clear();

            // Assert
            Assert.Equal(0, buffer.Count);
            Assert.True(buffer.IsEmpty);
            Assert.False(buffer.IsFull);
        }

        [Fact]
        public void Clear_AfterOverwrite_ResetsCorrectly()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            for (int i = 0; i < 10; i++)
            {
                buffer.Add(i);
            }

            // Act
            buffer.Clear();

            // Assert
            Assert.Equal(0, buffer.Count);
            Assert.Empty(buffer.ToList());
        }

        [Fact]
        public void Clear_CanReuseAfterClear()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Clear();

            // Act
            buffer.Add(3);
            buffer.Add(4);

            // Assert
            Assert.Equal(2, buffer.Count);
            var items = buffer.ToList();
            Assert.Contains(3, items);
            Assert.Contains(4, items);
        }

        #endregion

        #region ToList/ToArray Tests

        [Fact]
        public void ToList_EmptyBuffer_ReturnsEmptyList()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act
            var items = buffer.ToList();

            // Assert
            Assert.Empty(items);
        }

        [Fact]
        public void ToList_PartialBuffer_ReturnsCorrectOrder()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            var items = buffer.ToList();

            // Assert
            Assert.Equal(3, items.Count);
            Assert.Equal(1, items[0]);
            Assert.Equal(2, items[1]);
            Assert.Equal(3, items[2]);
        }

        [Fact]
        public void ToList_FullBuffer_ReturnsCorrectOrder()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            var items = buffer.ToList();

            // Assert
            Assert.Equal(3, items.Count);
            Assert.Equal(1, items[0]);
            Assert.Equal(2, items[1]);
            Assert.Equal(3, items[2]);
        }

        [Fact]
        public void ToArray_ReturnsSameContentAsToList()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            var list = buffer.ToList();
            var array = buffer.ToArray();

            // Assert
            Assert.Equal(list.Count, array.Length);
            for (int i = 0; i < list.Count; i++)
            {
                Assert.Equal(list[i], array[i]);
            }
        }

        #endregion

        #region CountWhere Tests

        [Fact]
        public void CountWhere_EmptyBuffer_ReturnsZero()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act
            var count = buffer.CountWhere(x => x > 0);

            // Assert
            Assert.Equal(0, count);
        }

        [Fact]
        public void CountWhere_NoMatches_ReturnsZero()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);
            buffer.Add(3);
            buffer.Add(5);

            // Act
            var count = buffer.CountWhere(x => x % 2 == 0);

            // Assert
            Assert.Equal(0, count);
        }

        [Fact]
        public void CountWhere_AllMatch_ReturnsCount()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(2);
            buffer.Add(4);
            buffer.Add(6);

            // Act
            var count = buffer.CountWhere(x => x % 2 == 0);

            // Assert
            Assert.Equal(3, count);
        }

        [Fact]
        public void CountWhere_SomeMatches_ReturnsCorrectCount()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(10);
            for (int i = 0; i < 10; i++)
            {
                buffer.Add(i);
            }

            // Act
            var count = buffer.CountWhere(x => x % 2 == 0);

            // Assert
            Assert.Equal(5, count);
        }

        [Fact]
        public void CountWhere_WithNullPredicate_ThrowsArgumentNullException()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => buffer.CountWhere(null!));
        }

        [Fact]
        public void CountWhere_AfterWraparound_WorksCorrectly()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            for (int i = 0; i < 10; i++)
            {
                buffer.Add(i);
            }

            // Act
            var count = buffer.CountWhere(x => x >= 7);

            // Assert
            Assert.Equal(3, count); // 7, 8, 9
        }

        #endregion

        #region PeekNewest Tests

        [Fact]
        public void PeekNewest_EmptyBuffer_ThrowsInvalidOperationException()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => buffer.PeekNewest());
        }

        [Fact]
        public void PeekNewest_SingleItem_ReturnsThatItem()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(42);

            // Act
            var item = buffer.PeekNewest();

            // Assert
            Assert.Equal(42, item);
        }

        [Fact]
        public void PeekNewest_MultipleItems_ReturnsLastAdded()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            var item = buffer.PeekNewest();

            // Assert
            Assert.Equal(3, item);
        }

        [Fact]
        public void PeekNewest_AfterOverwrite_ReturnsNewest()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Add(4);

            // Act
            var item = buffer.PeekNewest();

            // Assert
            Assert.Equal(4, item);
        }

        [Fact]
        public void TryPeekNewest_EmptyBuffer_ReturnsFalse()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act
            var result = buffer.TryPeekNewest(out var item);

            // Assert
            Assert.False(result);
            Assert.Equal(default, item);
        }

        [Fact]
        public void TryPeekNewest_WithItems_ReturnsTrueAndItem()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            var result = buffer.TryPeekNewest(out var item);

            // Assert
            Assert.True(result);
            Assert.Equal(3, item);
        }

        #endregion

        #region PeekOldest Tests

        [Fact]
        public void PeekOldest_EmptyBuffer_ThrowsInvalidOperationException()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => buffer.PeekOldest());
        }

        [Fact]
        public void PeekOldest_SingleItem_ReturnsThatItem()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(42);

            // Act
            var item = buffer.PeekOldest();

            // Assert
            Assert.Equal(42, item);
        }

        [Fact]
        public void PeekOldest_MultipleItems_ReturnsFirstAdded()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            var item = buffer.PeekOldest();

            // Assert
            Assert.Equal(1, item);
        }

        [Fact]
        public void PeekOldest_AfterOverwrite_ReturnsOldestRemaining()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Add(4);

            // Act
            var item = buffer.PeekOldest();

            // Assert
            Assert.Equal(2, item); // 1 was overwritten
        }

        [Fact]
        public void TryPeekOldest_EmptyBuffer_ReturnsFalse()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act
            var result = buffer.TryPeekOldest(out var item);

            // Assert
            Assert.False(result);
            Assert.Equal(default, item);
        }

        [Fact]
        public void TryPeekOldest_WithItems_ReturnsTrueAndItem()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            // Act
            var result = buffer.TryPeekOldest(out var item);

            // Assert
            Assert.True(result);
            Assert.Equal(1, item);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void SingleItemCapacity_PeekOperations_WorkCorrectly()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(1);
            buffer.Add(42);

            // Act & Assert
            Assert.Equal(42, buffer.PeekNewest());
            Assert.Equal(42, buffer.PeekOldest());

            // Act - Add new item
            buffer.Add(43);

            // Assert
            Assert.Equal(43, buffer.PeekNewest());
            Assert.Equal(43, buffer.PeekOldest());
        }

        [Fact]
        public void SingleItemCapacity_ToList_ReturnsSingleItem()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(1);
            buffer.Add(42);
            buffer.Add(43);
            buffer.Add(44);

            // Act
            var items = buffer.ToList();

            // Assert
            Assert.Single(items);
            Assert.Equal(44, items[0]);
        }

        [Fact]
        public void LargeCapacity_HandlesManyOperations()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(10000);

            // Act
            for (int i = 0; i < 20000; i++)
            {
                buffer.Add(i);
            }

            // Assert
            Assert.Equal(10000, buffer.Count);
            var items = buffer.ToList();
            Assert.Equal(10000, items.Count);
            Assert.Equal(10000, items[0]);
            Assert.Equal(19999, items[9999]);
        }

        [Fact]
        public void ReferenceType_StoresReferencesCorrectly()
        {
            // Arrange
            var buffer = new CircularBuffer<string>(3);
            var item1 = "first";
            var item2 = "second";
            var item3 = "third";

            // Act
            buffer.Add(item1);
            buffer.Add(item2);
            buffer.Add(item3);

            // Assert
            var items = buffer.ToList();
            Assert.Same(item1, items[0]);
            Assert.Same(item2, items[1]);
            Assert.Same(item3, items[2]);
        }

        [Fact]
        public void NullableType_HandlesNullValues()
        {
            // Arrange
            var buffer = new CircularBuffer<string?>(5);

            // Act
            buffer.Add("first");
            buffer.Add(null);
            buffer.Add("third");

            // Assert
            var items = buffer.ToList();
            Assert.Equal(3, items.Count);
            Assert.Equal("first", items[0]);
            Assert.Null(items[1]);
            Assert.Equal("third", items[2]);
        }

        [Fact]
        public void RapidAddRemove_MaintainsConsistency()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(5);

            // Act - Rapid adds and clears
            for (int iteration = 0; iteration < 100; iteration++)
            {
                for (int i = 0; i < 10; i++)
                {
                    buffer.Add(i);
                }
                buffer.Clear();
            }

            // Assert
            Assert.Equal(0, buffer.Count);
            Assert.Empty(buffer.ToList());
        }

        [Fact]
        public void CountWhere_ComplexPredicate_WorksCorrectly()
        {
            // Arrange
            var buffer = new CircularBuffer<int>(20);
            for (int i = 0; i < 20; i++)
            {
                buffer.Add(i);
            }

            // Act
            var count = buffer.CountWhere(x => x > 5 && x < 15 && x % 2 == 0);

            // Assert
            Assert.Equal(5, count); // 6, 8, 10, 12, 14
        }

        #endregion
    }
}
