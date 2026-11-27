using System.Linq.Expressions;
using ERMS.Application.Abstractions.Authentication;
using ERMS.Application.Abstractions.Persistence;
using ERMS.Application.Common.Exceptions;
using ERMS.Application.DTOs.Approvals;
using ERMS.Application.DTOs.Requests;
using ERMS.Application.Services;
using ERMS.Application.Validators;
using ERMS.Domain.Entities;
using ERMS.Domain.Enums;
using Moq;

namespace ERMS.Tests.Services;

/// <summary>
/// State transition / model-based tests for the Request lifecycle:
/// (Initial) -> Draft -> Pending -> Approved / Rejected / Cancelled (plus auto-approve Draft -> Approved).
/// Covers valid 0-switch transitions, guard failures (FR-15, FR-18..22), invalid transitions (sneak paths),
/// audit trail logging (FR-41), and multi-step (1-switch) end-to-end sequences.
/// </summary>
public class RequestStateTransitionTests
{
    private const int EmployeeId = 10;
    private const int OtherUserId = 999;
    private const int ManagerId = 20;

    private static RequestType LeaveType(bool requiresApproval = true, bool isActive = true) => new()
    {
        Id = 1,
        Name = "İzin",
        RequiresApproval = requiresApproval,
        IsActive = isActive
    };

    private static RequestType ExpenseType(bool requiresApproval = true, bool isActive = true) => new()
    {
        Id = 2,
        Name = "Masraf",
        RequiresApproval = requiresApproval,
        IsActive = isActive
    };

    private static RequestType EquipmentType(bool requiresApproval = false, bool isActive = true) => new()
    {
        Id = 3,
        Name = "Ekipman",
        RequiresApproval = requiresApproval,
        IsActive = isActive
    };

    private static Mock<ICurrentUserService> CurrentUserMock(int userId = EmployeeId, UserRole role = UserRole.Employee)
    {
        var mock = new Mock<ICurrentUserService>();
        mock.SetupGet(c => c.UserId).Returns(userId);
        mock.SetupGet(c => c.Role).Returns(role);
        return mock;
    }

    private static Mock<IRepository<RequestType>> RequestTypeRepoFor(params RequestType[] types)
    {
        var repo = new Mock<IRepository<RequestType>>();
        repo.Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RequestType, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<RequestType, bool>> predicate, CancellationToken _) =>
                types.FirstOrDefault(predicate.Compile()));

        repo.Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => types.FirstOrDefault(t => t.Id == id));

        return repo;
    }

    private static RequestService CreateRequestService(
        Mock<IRepository<Request>> requestRepo,
        Mock<IRepository<RequestType>> requestTypeRepo,
        Mock<IRepository<RequestHistory>> historyRepo,
        Mock<ICurrentUserService> currentUser,
        Mock<IUnitOfWork>? unitOfWork = null,
        Mock<IRequestQueryRepository>? queryRepo = null)
    {
        unitOfWork ??= new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        return new RequestService(
            requestRepo.Object,
            requestTypeRepo.Object,
            historyRepo.Object,
            queryRepo?.Object ?? Mock.Of<IRequestQueryRepository>(),
            currentUser.Object,
            unitOfWork.Object,
            new CreateRequestDtoValidator(),
            new UpdateRequestDtoValidator());
    }

    private static ApprovalService CreateApprovalService(
        Mock<IRepository<Request>> requestRepo,
        Mock<IRepository<Approval>> approvalRepo,
        Mock<IRepository<RequestHistory>> historyRepo,
        Mock<IRepository<User>> userRepo,
        Mock<ICurrentUserService> currentUser,
        Mock<IUnitOfWork>? unitOfWork = null)
    {
        unitOfWork ??= new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        return new ApprovalService(
            requestRepo.Object,
            approvalRepo.Object,
            historyRepo.Object,
            userRepo.Object,
            Mock.Of<IRequestQueryRepository>(),
            currentUser.Object,
            unitOfWork.Object,
            new ApproveRequestDtoValidator(),
            new RejectRequestDtoValidator());
    }

    private static Mock<IRepository<User>> UserRepoWithSubordinate()
    {
        var employee = new User
        {
            Id = EmployeeId,
            FirstName = "Ahmet",
            LastName = "Yılmaz",
            Email = "ahmet@sirket.com",
            PasswordHash = "hash",
            ManagerId = ManagerId
        };
        var manager = new User
        {
            Id = ManagerId,
            FirstName = "Mehmet",
            LastName = "Kaya",
            Email = "mehmet@sirket.com",
            PasswordHash = "hash"
        };

        var userRepo = new Mock<IRepository<User>>();
        userRepo.Setup(u => u.GetByIdAsync(EmployeeId, It.IsAny<CancellationToken>())).ReturnsAsync(employee);
        userRepo.Setup(u => u.GetByIdAsync(ManagerId, It.IsAny<CancellationToken>())).ReturnsAsync(manager);
        return userRepo;
    }

    // =========================================================================
    // 1. Valid 0-switch transitions & audit log (FR-18..24, FR-30, FR-41)
    // =========================================================================

    [Fact]
    [Trait("ID", "ST-01")]
    public async Task CreateAsync_ExpenseSaveAsDraftTrue_CreatesDraftWithoutHistory()
    {
        // Arrange
        var requestRepo = new Mock<IRepository<Request>>();
        Request? saved = null;
        requestRepo.Setup(r => r.AddAsync(It.IsAny<Request>(), It.IsAny<CancellationToken>()))
            .Callback<Request, CancellationToken>((r, _) => saved = r)
            .Returns(Task.CompletedTask);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(ExpenseType()), historyRepo, CurrentUserMock());

        var dto = new CreateRequestDto(
            RequestTypeId: 2,
            Title: "Yol masrafı taslağı",
            Description: "Fatura sonra eklenecek",
            StartDate: null,
            EndDate: null,
            Amount: null,
            Priority: RequestPriority.Normal,
            SaveAsDraft: true);

        // Act
        var result = await sut.CreateAsync(dto);

        // Assert
        Assert.Equal("Draft", result.Status);
        Assert.NotNull(saved);
        Assert.Equal(RequestStatus.Draft, saved!.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-02")]
    public async Task CreateAsync_LeaveSubmittedRequiresApproval_TransitionsToPendingAndLogsHistory()
    {
        // Arrange
        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.AddAsync(It.IsAny<Request>(), It.IsAny<CancellationToken>()))
            .Callback<Request, CancellationToken>((r, _) => r.Id = 101)
            .Returns(Task.CompletedTask);

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType(requiresApproval: true)), historyRepo, CurrentUserMock());

        var dto = new CreateRequestDto(
            RequestTypeId: 1,
            Title: "Yıllık izin",
            Description: "Aralık tatili",
            StartDate: new DateTime(2026, 12, 1),
            EndDate: new DateTime(2026, 12, 5),
            Amount: null,
            Priority: RequestPriority.Normal,
            SaveAsDraft: false);

        // Act
        var result = await sut.CreateAsync(dto);

        // Assert
        Assert.Equal("Pending", result.Status);
        var entry = Assert.Single(historyLog);
        Assert.Equal(101, entry.RequestId);
        Assert.Equal(EmployeeId, entry.ChangedById);
        Assert.Equal(RequestStatus.Draft, entry.OldStatus);
        Assert.Equal(RequestStatus.Pending, entry.NewStatus);
    }

    [Fact]
    [Trait("ID", "ST-03")]
    public async Task CreateAsync_SubmittedNoApprovalRequired_TransitionsDirectlyToApprovedAndLogsHistory()
    {
        // Arrange
        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.AddAsync(It.IsAny<Request>(), It.IsAny<CancellationToken>()))
            .Callback<Request, CancellationToken>((r, _) => r.Id = 102)
            .Returns(Task.CompletedTask);

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(EquipmentType(requiresApproval: false)), historyRepo, CurrentUserMock());

        var dto = new CreateRequestDto(
            RequestTypeId: 3,
            Title: "Klavye talebi",
            Description: "Standart ofis ekipmanı",
            StartDate: null,
            EndDate: null,
            Amount: null,
            Priority: RequestPriority.Low,
            SaveAsDraft: false);

        // Act
        var result = await sut.CreateAsync(dto);

        // Assert
        Assert.Equal("Approved", result.Status);
        var entry = Assert.Single(historyLog);
        Assert.Equal(RequestStatus.Draft, entry.OldStatus);
        Assert.Equal(RequestStatus.Approved, entry.NewStatus);
    }

    [Fact]
    [Trait("ID", "ST-04")]
    public async Task CreateAsync_LeaveWithSameStartAndEndDate_TransitionsToPending()
    {
        // Arrange
        var requestRepo = new Mock<IRepository<Request>>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType(requiresApproval: true)), historyRepo, CurrentUserMock());

        var singleDay = new DateTime(2026, 12, 1);
        var dto = new CreateRequestDto(
            RequestTypeId: 1,
            Title: "Tek günlük izin",
            Description: "Hastane randevusu",
            StartDate: singleDay,
            EndDate: singleDay,
            Amount: null,
            Priority: RequestPriority.Normal,
            SaveAsDraft: false);

        // Act
        var result = await sut.CreateAsync(dto);

        // Assert
        Assert.Equal("Pending", result.Status);
    }

    [Fact]
    [Trait("ID", "ST-05")]
    public async Task CreateAsync_ExpenseWithPositiveAmountSubmitted_TransitionsToPending()
    {
        // Arrange
        var requestRepo = new Mock<IRepository<Request>>();
        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(ExpenseType(requiresApproval: true)), historyRepo, CurrentUserMock());

        var dto = new CreateRequestDto(
            RequestTypeId: 2,
            Title: "Ulaşım masrafı",
            Description: "Müşteri ziyareti taksi fişi",
            StartDate: null,
            EndDate: null,
            Amount: 250.50m,
            Priority: RequestPriority.High,
            SaveAsDraft: false);

        // Act
        var result = await sut.CreateAsync(dto);

        // Assert
        Assert.Equal("Pending", result.Status);
        var entry = Assert.Single(historyLog);
        Assert.Equal(RequestStatus.Draft, entry.OldStatus);
        Assert.Equal(RequestStatus.Pending, entry.NewStatus);
    }

    [Fact]
    [Trait("ID", "ST-06")]
    public async Task UpdateAsync_DraftOwnedByCaller_RemainsDraftWithoutHistory()
    {
        // Arrange
        var existing = new Request
        {
            Id = 5,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = RequestStatus.Draft,
            Title = "Eski başlık",
            Description = "Eski açıklama",
            Priority = RequestPriority.Low
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        var dto = new UpdateRequestDto(
            RequestTypeId: 1,
            Title: "Güncellenmiş taslak",
            Description: "Tarihler hâlâ boş bırakılabilir",
            StartDate: null,
            EndDate: null,
            Amount: null,
            Priority: RequestPriority.High);

        // Act
        var result = await sut.UpdateAsync(5, dto);

        // Assert
        Assert.Equal("Draft", result.Status);
        Assert.Equal("Güncellenmiş taslak", result.Title);
        Assert.Equal(RequestStatus.Draft, existing.Status);
        Assert.Equal(RequestPriority.High, existing.Priority);
        requestRepo.Verify(r => r.Update(existing), Times.Once);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-07")]
    public async Task SubmitAsync_ValidDraftRequiresApproval_TransitionsToPendingAndLogsHistory()
    {
        // Arrange
        var existing = new Request
        {
            Id = 10,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = RequestStatus.Draft,
            Title = "Yıllık izin",
            Description = "Aralık izni",
            StartDate = new DateTime(2026, 12, 1),
            EndDate = new DateTime(2026, 12, 4)
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType(requiresApproval: true)), historyRepo, CurrentUserMock());

        // Act
        var result = await sut.SubmitAsync(10);

        // Assert
        Assert.Equal("Pending", result.Status);
        Assert.Equal(RequestStatus.Pending, existing.Status);
        requestRepo.Verify(r => r.Update(existing), Times.Once);

        var entry = Assert.Single(historyLog);
        Assert.Equal(10, entry.RequestId);
        Assert.Equal(EmployeeId, entry.ChangedById);
        Assert.Equal(RequestStatus.Draft, entry.OldStatus);
        Assert.Equal(RequestStatus.Pending, entry.NewStatus);
    }

    [Fact]
    [Trait("ID", "ST-08")]
    public async Task SubmitAsync_ValidDraftNoApprovalRequired_TransitionsToApprovedAndLogsHistory()
    {
        // Arrange
        var existing = new Request
        {
            Id = 11,
            RequesterId = EmployeeId,
            RequestTypeId = 3,
            Status = RequestStatus.Draft,
            Title = "Monitör standı",
            Description = "Onaysız standart ekipman"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(EquipmentType(requiresApproval: false)), historyRepo, CurrentUserMock());

        // Act
        var result = await sut.SubmitAsync(11);

        // Assert
        Assert.Equal("Approved", result.Status);
        Assert.Equal(RequestStatus.Approved, existing.Status);

        var entry = Assert.Single(historyLog);
        Assert.Equal(RequestStatus.Draft, entry.OldStatus);
        Assert.Equal(RequestStatus.Approved, entry.NewStatus);
    }

    [Fact]
    [Trait("ID", "ST-09")]
    public async Task CancelAsync_PendingRequestOwnedByCaller_TransitionsToCancelledAndLogsHistory()
    {
        // Arrange
        var existing = new Request
        {
            Id = 12,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = RequestStatus.Pending,
            Title = "İzin talebi",
            Description = "İptal edilecek"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        // Act
        var result = await sut.CancelAsync(12);

        // Assert
        Assert.Equal("Cancelled", result.Status);
        Assert.Equal(RequestStatus.Cancelled, existing.Status);

        var entry = Assert.Single(historyLog);
        Assert.Equal(12, entry.RequestId);
        Assert.Equal(EmployeeId, entry.ChangedById);
        Assert.Equal(RequestStatus.Pending, entry.OldStatus);
        Assert.Equal(RequestStatus.Cancelled, entry.NewStatus);
    }

    [Fact]
    [Trait("ID", "ST-10")]
    public async Task CancelAsync_PendingRequestWithInactiveType_StillTransitionsToCancelled()
    {
        // Arrange
        var inactiveType = LeaveType(requiresApproval: true, isActive: false);
        var existing = new Request
        {
            Id = 13,
            RequesterId = EmployeeId,
            RequestTypeId = inactiveType.Id,
            Status = RequestStatus.Pending,
            Title = "Eski türdeki talep",
            Description = "Tür sonradan pasife alındı"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(13, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(inactiveType), historyRepo, CurrentUserMock());

        // Act
        var result = await sut.CancelAsync(13);

        // Assert
        Assert.Equal("Cancelled", result.Status);
        Assert.Equal("İzin", result.Type);
        Assert.Equal(RequestStatus.Cancelled, existing.Status);
        Assert.Single(historyLog);
    }

    // =========================================================================
    // 2. Guard failures on Draft -> Submit & Cancel (must keep state unchanged)
    // =========================================================================

    [Fact]
    [Trait("ID", "ST-11")]
    public async Task SubmitAsync_LeaveDraftMissingStartDate_ThrowsValidationAndRemainsDraft()
    {
        // Arrange
        var existing = new Request
        {
            Id = 20,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = RequestStatus.Draft,
            Title = "Eksik izin taslağı",
            Description = "Başlangıç yok",
            StartDate = null,
            EndDate = new DateTime(2026, 12, 5)
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => sut.SubmitAsync(20));

        Assert.Contains("startDate", ex.Errors.Keys);
        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-12")]
    public async Task SubmitAsync_LeaveDraftMissingEndDate_ThrowsValidationAndRemainsDraft()
    {
        // Arrange
        var existing = new Request
        {
            Id = 21,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = RequestStatus.Draft,
            Title = "Eksik izin taslağı",
            Description = "Bitiş yok",
            StartDate = new DateTime(2026, 12, 1),
            EndDate = null
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(21, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => sut.SubmitAsync(21));

        Assert.Contains("endDate", ex.Errors.Keys);
        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-13")]
    public async Task SubmitAsync_LeaveDraftEndDateBeforeStartDate_ThrowsValidationAndRemainsDraft()
    {
        // Arrange
        var existing = new Request
        {
            Id = 22,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = RequestStatus.Draft,
            Title = "Hatalı tarihli izin",
            Description = "Bitiş başlangıçtan önce",
            StartDate = new DateTime(2026, 12, 10),
            EndDate = new DateTime(2026, 12, 1)
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(22, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => sut.SubmitAsync(22));

        Assert.Contains("endDate", ex.Errors.Keys);
        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-14")]
    public async Task SubmitAsync_ExpenseDraftMissingAmount_ThrowsValidationAndRemainsDraft()
    {
        // Arrange
        var existing = new Request
        {
            Id = 23,
            RequesterId = EmployeeId,
            RequestTypeId = 2,
            Status = RequestStatus.Draft,
            Title = "Tutarsız masraf",
            Description = "Tutar girilmemiş",
            Amount = null
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(23, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(ExpenseType()), historyRepo, CurrentUserMock());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => sut.SubmitAsync(23));

        Assert.Contains("amount", ex.Errors.Keys);
        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-15")]
    public async Task SubmitAsync_DraftWithInactiveRequestType_ThrowsNotFoundAndRemainsDraft()
    {
        // Arrange
        var inactiveType = LeaveType(requiresApproval: true, isActive: false);
        var existing = new Request
        {
            Id = 24,
            RequesterId = EmployeeId,
            RequestTypeId = inactiveType.Id,
            Status = RequestStatus.Draft,
            Title = "Pasif tür taslağı",
            Description = "Gönderilememeli",
            StartDate = new DateTime(2026, 12, 1),
            EndDate = new DateTime(2026, 12, 2)
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(24, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(inactiveType), historyRepo, CurrentUserMock());

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundAppException>(() => sut.SubmitAsync(24));

        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-16")]
    public async Task SubmitAsync_DraftNotOwnedByCaller_ThrowsForbiddenAndRemainsDraft()
    {
        // Arrange
        var existing = new Request
        {
            Id = 25,
            RequesterId = OtherUserId,
            RequestTypeId = 1,
            Status = RequestStatus.Draft,
            Title = "Başkasının taslağı",
            Description = "Yetkisiz gönderim",
            StartDate = new DateTime(2026, 12, 1),
            EndDate = new DateTime(2026, 12, 2)
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(25, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock(EmployeeId));

        // Act & Assert
        await Assert.ThrowsAsync<ForbiddenAppException>(() => sut.SubmitAsync(25));

        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-17")]
    public async Task SubmitAsync_NonExistentRequest_ThrowsNotFound()
    {
        // Arrange
        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Request?)null);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundAppException>(() => sut.SubmitAsync(999));
    }

    [Fact]
    [Trait("ID", "ST-18")]
    public async Task CancelAsync_PendingNotOwnedByCaller_ThrowsForbiddenAndRemainsPending()
    {
        // Arrange
        var existing = new Request
        {
            Id = 26,
            RequesterId = OtherUserId,
            RequestTypeId = 1,
            Status = RequestStatus.Pending,
            Title = "Başkasının bekleyen talebi",
            Description = "Yetkisiz iptal"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(26, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock(EmployeeId));

        // Act & Assert
        await Assert.ThrowsAsync<ForbiddenAppException>(() => sut.CancelAsync(26));

        Assert.Equal(RequestStatus.Pending, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-19")]
    public async Task CancelAsync_NonExistentRequest_ThrowsNotFound()
    {
        // Arrange
        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Request?)null);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundAppException>(() => sut.CancelAsync(999));
    }

    // =========================================================================
    // 3. Invalid state transitions / sneak paths (all disallowed State x Event)
    // =========================================================================

    [Theory]
    [Trait("ID", "ST-20")]
    [InlineData(RequestStatus.Approved)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Cancelled)]
    public async Task UpdateAsync_TerminalStatus_ThrowsConflictAndDoesNotMutate(RequestStatus terminalStatus)
    {
        // Arrange
        var existing = new Request
        {
            Id = 30,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = terminalStatus,
            Title = "Sonuçlanmış talep",
            Description = "Değiştirilemez"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(30, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        var dto = new UpdateRequestDto(1, "Yeni başlık", "Yeni açıklama", null, null, null, RequestPriority.Normal);

        // Act & Assert
        await Assert.ThrowsAsync<ConflictAppException>(() => sut.UpdateAsync(30, dto));

        Assert.Equal(terminalStatus, existing.Status);
        Assert.Equal("Sonuçlanmış talep", existing.Title);
        requestRepo.Verify(r => r.Update(It.IsAny<Request>()), Times.Never);
    }

    [Theory]
    [Trait("ID", "ST-21")]
    [InlineData(RequestStatus.Pending)]
    [InlineData(RequestStatus.Approved)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Cancelled)]
    public async Task SubmitAsync_NonDraftStatus_ThrowsConflictAndDoesNotLogHistory(RequestStatus status)
    {
        // Arrange
        var existing = new Request
        {
            Id = 31,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = status,
            Title = "Taslak olmayan talep",
            Description = "Gönderilemez",
            StartDate = new DateTime(2026, 12, 1),
            EndDate = new DateTime(2026, 12, 2)
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(31, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        // Act & Assert
        await Assert.ThrowsAsync<ConflictAppException>(() => sut.SubmitAsync(31));

        Assert.Equal(status, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [Trait("ID", "ST-22")]
    [InlineData(RequestStatus.Draft)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Cancelled)]
    public async Task CancelAsync_NonPendingStatus_ThrowsConflictAndDoesNotLogHistory(RequestStatus status)
    {
        // Arrange
        var existing = new Request
        {
            Id = 32,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = status,
            Title = "İptal edilemez talep",
            Description = "Pending değil"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(32, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        // Act & Assert
        await Assert.ThrowsAsync<ConflictAppException>(() => sut.CancelAsync(32));

        Assert.Equal(status, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [Trait("ID", "ST-23")]
    [InlineData(RequestStatus.Draft)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Cancelled)]
    public async Task ApproveAsync_NonPendingStatus_ThrowsConflictAndDoesNotLogHistory(RequestStatus status)
    {
        // Arrange
        var existing = new Request
        {
            Id = 33,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = status,
            Title = "Onaylanamaz talep",
            Description = "Pending değil"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(33, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var approvalRepo = new Mock<IRepository<Approval>>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateApprovalService(
            requestRepo,
            approvalRepo,
            historyRepo,
            UserRepoWithSubordinate(),
            CurrentUserMock(ManagerId, UserRole.Manager));

        // Act & Assert
        await Assert.ThrowsAsync<ConflictAppException>(() => sut.ApproveAsync(33, new ApproveRequestDto("Onay")));

        Assert.Equal(status, existing.Status);
        approvalRepo.Verify(a => a.AddAsync(It.IsAny<Approval>(), It.IsAny<CancellationToken>()), Times.Never);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [Trait("ID", "ST-24")]
    [InlineData(RequestStatus.Draft)]
    [InlineData(RequestStatus.Approved)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Cancelled)]
    public async Task RejectAsync_NonPendingStatus_ThrowsConflictAndDoesNotLogHistory(RequestStatus status)
    {
        // Arrange
        var existing = new Request
        {
            Id = 34,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = status,
            Title = "Reddedilemez talep",
            Description = "Pending değil"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(34, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var approvalRepo = new Mock<IRepository<Approval>>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateApprovalService(
            requestRepo,
            approvalRepo,
            historyRepo,
            UserRepoWithSubordinate(),
            CurrentUserMock(ManagerId, UserRole.Manager));

        // Act & Assert
        await Assert.ThrowsAsync<ConflictAppException>(() => sut.RejectAsync(34, new RejectRequestDto("Uygun değil.")));

        Assert.Equal(status, existing.Status);
        approvalRepo.Verify(a => a.AddAsync(It.IsAny<Approval>(), It.IsAny<CancellationToken>()), Times.Never);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // 4. Multi-step (1-switch / full lifecycle) model-based sequences
    // =========================================================================

    [Fact]
    [Trait("ID", "ST-25")]
    public async Task Lifecycle_DraftUpdatedSubmittedThenCancelled_TransitionsAndLocksTerminalState()
    {
        // Arrange — stateful in-memory repository backing a full request lifecycle
        var store = new Dictionary<int, Request>();
        var nextId = 50;
        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.AddAsync(It.IsAny<Request>(), It.IsAny<CancellationToken>()))
            .Callback<Request, CancellationToken>((r, _) =>
            {
                r.Id = nextId++;
                store[r.Id] = r;
            })
            .Returns(Task.CompletedTask);
        requestRepo.Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => store.GetValueOrDefault(id));

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType(requiresApproval: true)), historyRepo, CurrentUserMock());

        // Act & Assert — Step 1: Create incomplete Leave Draft (Initial -> Draft)
        var created = await sut.CreateAsync(new CreateRequestDto(
            1, "Taslak izin", "Tarihler sonra eklenecek", null, null, null, RequestPriority.Normal, SaveAsDraft: true));
        Assert.Equal("Draft", created.Status);
        Assert.Empty(historyLog);

        // Step 2: Update Draft with valid dates (Draft -> Draft)
        var updated = await sut.UpdateAsync(created.Id, new UpdateRequestDto(
            1, "Güncel izin", "Tarihler eklendi", new DateTime(2026, 12, 10), new DateTime(2026, 12, 12), null, RequestPriority.Normal));
        Assert.Equal("Draft", updated.Status);
        Assert.Empty(historyLog);

        // Step 3: Submit Draft (Draft -> Pending)
        var submitted = await sut.SubmitAsync(created.Id);
        Assert.Equal("Pending", submitted.Status);
        Assert.Single(historyLog);
        Assert.Equal((RequestStatus.Draft, RequestStatus.Pending), (historyLog[0].OldStatus, historyLog[0].NewStatus));

        // Step 4: Cancel Pending (Pending -> Cancelled)
        var cancelled = await sut.CancelAsync(created.Id);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(2, historyLog.Count);
        Assert.Equal((RequestStatus.Pending, RequestStatus.Cancelled), (historyLog[1].OldStatus, historyLog[1].NewStatus));

        // Step 5: Verify terminal state immutability — no outgoing transitions allowed
        await Assert.ThrowsAsync<ConflictAppException>(() => sut.UpdateAsync(
            created.Id, new UpdateRequestDto(1, "X", "Y", null, null, null, RequestPriority.Low)));
        await Assert.ThrowsAsync<ConflictAppException>(() => sut.SubmitAsync(created.Id));
        await Assert.ThrowsAsync<ConflictAppException>(() => sut.CancelAsync(created.Id));
        Assert.Equal(RequestStatus.Cancelled, store[created.Id].Status);
        Assert.Equal(2, historyLog.Count);
    }

    [Theory]
    [Trait("ID", "ST-26")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lifecycle_DraftSubmittedThenApprovedOrRejected_LogsFullAuditTrailAndLocksTerminalState(bool approve)
    {
        // Arrange — stateful repositories shared between RequestService (employee) and ApprovalService (manager)
        var store = new Dictionary<int, Request>();
        var nextId = 60;
        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.AddAsync(It.IsAny<Request>(), It.IsAny<CancellationToken>()))
            .Callback<Request, CancellationToken>((r, _) =>
            {
                r.Id = nextId++;
                store[r.Id] = r;
            })
            .Returns(Task.CompletedTask);
        requestRepo.Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => store.GetValueOrDefault(id));

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var approvals = new List<Approval>();
        var approvalRepo = new Mock<IRepository<Approval>>();
        approvalRepo.Setup(a => a.AddAsync(It.IsAny<Approval>(), It.IsAny<CancellationToken>()))
            .Callback<Approval, CancellationToken>((a, _) => approvals.Add(a))
            .Returns(Task.CompletedTask);

        var requestService = CreateRequestService(
            requestRepo,
            RequestTypeRepoFor(ExpenseType(requiresApproval: true)),
            historyRepo,
            CurrentUserMock(EmployeeId, UserRole.Employee));

        var approvalService = CreateApprovalService(
            requestRepo,
            approvalRepo,
            historyRepo,
            UserRepoWithSubordinate(),
            CurrentUserMock(ManagerId, UserRole.Manager));

        // Act & Assert — Step 1: Create Draft -> Step 2: Submit -> Pending
        var created = await requestService.CreateAsync(new CreateRequestDto(
            2, "Konferans masrafı", "Uçak bileti", null, null, 1500m, RequestPriority.Normal, SaveAsDraft: true));
        Assert.Equal("Draft", created.Status);

        var submitted = await requestService.SubmitAsync(created.Id);
        Assert.Equal("Pending", submitted.Status);

        // Step 3: Manager decides (Pending -> Approved or Pending -> Rejected)
        var expectedTerminal = approve ? RequestStatus.Approved : RequestStatus.Rejected;
        var decisionResult = approve
            ? await approvalService.ApproveAsync(created.Id, new ApproveRequestDto("Onaylandı."))
            : await approvalService.RejectAsync(created.Id, new RejectRequestDto("Bütçe dışı."));

        Assert.Equal(expectedTerminal.ToString(), decisionResult.Status);
        Assert.Equal(expectedTerminal, store[created.Id].Status);

        var approvalRecord = Assert.Single(approvals);
        Assert.Equal(approve ? ApprovalDecision.Approved : ApprovalDecision.Rejected, approvalRecord.Decision);

        Assert.Equal(2, historyLog.Count);
        Assert.Equal((RequestStatus.Draft, RequestStatus.Pending), (historyLog[0].OldStatus, historyLog[0].NewStatus));
        Assert.Equal((RequestStatus.Pending, expectedTerminal), (historyLog[1].OldStatus, historyLog[1].NewStatus));

        // Step 4: Verify terminal state rejects all further transitions
        await Assert.ThrowsAsync<ConflictAppException>(() => requestService.UpdateAsync(
            created.Id, new UpdateRequestDto(2, "X", "Y", null, null, 100m, RequestPriority.Low)));
        await Assert.ThrowsAsync<ConflictAppException>(() => requestService.SubmitAsync(created.Id));
        await Assert.ThrowsAsync<ConflictAppException>(() => requestService.CancelAsync(created.Id));
        await Assert.ThrowsAsync<ConflictAppException>(() => approvalService.ApproveAsync(created.Id, new ApproveRequestDto(null)));
        await Assert.ThrowsAsync<ConflictAppException>(() => approvalService.RejectAsync(created.Id, new RejectRequestDto("Tekrar red")));
        Assert.Equal(expectedTerminal, store[created.Id].Status);
        Assert.Equal(2, historyLog.Count);
    }

    // =========================================================================
    // 5. Additional transition edge cases & state history queries (FR-22, FR-26, FR-30, FR-42)
    // =========================================================================

    [Fact]
    [Trait("ID", "ST-27")]
    public async Task UpdateAsync_NonExistentRequest_ThrowsNotFound()
    {
        // Arrange
        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Request?)null);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, CurrentUserMock());

        var dto = new UpdateRequestDto(1, "Başlık", "Açıklama", null, null, null, RequestPriority.Normal);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundAppException>(() => sut.UpdateAsync(999, dto));
    }

    [Fact]
    [Trait("ID", "ST-28")]
    public async Task CancelAsync_PendingRequestWithMissingRequestType_TransitionsToCancelledWithFallbackName()
    {
        // Arrange
        var existing = new Request
        {
            Id = 14,
            RequesterId = EmployeeId,
            RequestTypeId = 999,
            Status = RequestStatus.Pending,
            Title = "Türü silinmiş talep",
            Description = "İptal yine de çalışmalı"
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(14, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyLog = new List<RequestHistory>();
        var historyRepo = new Mock<IRepository<RequestHistory>>();
        historyRepo.Setup(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()))
            .Callback<RequestHistory, CancellationToken>((h, _) => historyLog.Add(h))
            .Returns(Task.CompletedTask);

        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(), historyRepo, CurrentUserMock());

        // Act
        var result = await sut.CancelAsync(14);

        // Assert
        Assert.Equal("Cancelled", result.Status);
        Assert.Equal("Bilinmiyor", result.Type);
        Assert.Equal(RequestStatus.Cancelled, existing.Status);
        var entry = Assert.Single(historyLog);
        Assert.Equal((RequestStatus.Pending, RequestStatus.Cancelled), (entry.OldStatus, entry.NewStatus));
    }

    [Fact]
    [Trait("ID", "ST-29")]
    public async Task SubmitAsync_UnauthenticatedUser_ThrowsUnauthorizedAndRemainsDraft()
    {
        // Arrange
        var existing = new Request
        {
            Id = 27,
            RequesterId = EmployeeId,
            RequestTypeId = 1,
            Status = RequestStatus.Draft,
            Title = "Taslak",
            Description = "Anonim deneme",
            StartDate = new DateTime(2026, 12, 1),
            EndDate = new DateTime(2026, 12, 2)
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(27, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var unauthUser = new Mock<ICurrentUserService>();
        unauthUser.SetupGet(c => c.UserId).Returns((int?)null);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(LeaveType()), historyRepo, unauthUser);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAppException>(() => sut.SubmitAsync(27));

        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("ID", "ST-30")]
    public async Task GetDetailAsync_OwnerWithStateHistory_ReturnsChronologicalTransitionsAndDetails()
    {
        // Arrange
        var employee = new User { Id = EmployeeId, FirstName = "Ahmet", LastName = "Yılmaz", Email = "a@b.com", PasswordHash = "h", ManagerId = ManagerId };
        var manager = new User { Id = ManagerId, FirstName = "Mehmet", LastName = "Kaya", Email = "m@b.com", PasswordHash = "h" };
        var t1 = new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc);

        var request = new Request
        {
            Id = 40,
            RequesterId = EmployeeId,
            Requester = employee,
            RequestTypeId = 1,
            RequestType = LeaveType(),
            Title = "Onaylanmış izin",
            Description = "Geçmiş kontrolü",
            Status = RequestStatus.Approved,
            Priority = RequestPriority.Normal,
            StartDate = new DateTime(2026, 12, 5),
            EndDate = new DateTime(2026, 12, 7),
            CreatedAt = t1,
            UpdatedAt = t2,
            History =
            [
                new RequestHistory { RequestId = 40, ChangedById = EmployeeId, ChangedBy = employee, OldStatus = RequestStatus.Draft, NewStatus = RequestStatus.Pending, Note = "Talep gönderildi.", ChangedAt = t1 },
                new RequestHistory { RequestId = 40, ChangedById = ManagerId, ChangedBy = manager, OldStatus = RequestStatus.Pending, NewStatus = RequestStatus.Approved, Note = "Talep onaylandı.", ChangedAt = t2 }
            ],
            Comments =
            [
                new RequestComment { Id = 2, RequestId = 40, AuthorId = ManagerId, Author = manager, Content = "İkinci yorum", CreatedAt = t2 },
                new RequestComment { Id = 1, RequestId = 40, AuthorId = EmployeeId, Author = employee, Content = "İlk yorum", CreatedAt = t1 }
            ],
            Attachments =
            [
                new RequestAttachment { Id = 1, RequestId = 40, FileName = "eski.pdf", FilePath = "a.pdf", ContentType = "application/pdf", FileSize = 100, UploadedAt = t1 },
                new RequestAttachment { Id = 2, RequestId = 40, FileName = "yeni.pdf", FilePath = "b.pdf", ContentType = "application/pdf", FileSize = 200, UploadedAt = t2 }
            ]
        };

        var queryRepo = new Mock<IRequestQueryRepository>();
        queryRepo.Setup(q => q.GetDetailAsync(40, It.IsAny<CancellationToken>())).ReturnsAsync(request);

        var sut = CreateRequestService(
            new Mock<IRepository<Request>>(),
            RequestTypeRepoFor(LeaveType()),
            new Mock<IRepository<RequestHistory>>(),
            CurrentUserMock(EmployeeId, UserRole.Employee),
            queryRepo: queryRepo);

        // Act
        var detail = await sut.GetDetailAsync(40);

        // Assert
        Assert.Equal("Approved", detail.Status);
        Assert.Equal(2, detail.History.Count);
        Assert.Equal(("Draft", "Pending", "Ahmet Yılmaz"), (detail.History[0].OldStatus, detail.History[0].NewStatus, detail.History[0].ChangedByName));
        Assert.Equal(("Pending", "Approved", "Mehmet Kaya"), (detail.History[1].OldStatus, detail.History[1].NewStatus, detail.History[1].ChangedByName));
        Assert.Equal(["İlk yorum", "İkinci yorum"], detail.Comments.Select(c => c.Content).ToArray());
        Assert.Equal(["yeni.pdf", "eski.pdf"], detail.Attachments.Select(a => a.FileName).ToArray());
    }

    [Fact]
    [Trait("ID", "ST-31")]
    public async Task GetDetailAsync_DirectManagerOfRequester_ReturnsRequestWithStateHistory()
    {
        // Arrange
        var employee = new User { Id = EmployeeId, FirstName = "Ahmet", LastName = "Yılmaz", Email = "a@b.com", PasswordHash = "h", ManagerId = ManagerId };
        var request = new Request
        {
            Id = 41,
            RequesterId = EmployeeId,
            Requester = employee,
            RequestTypeId = 1,
            RequestType = LeaveType(),
            Title = "Bekleyen izin",
            Description = "Yönetici görünümü",
            Status = RequestStatus.Pending,
            Priority = RequestPriority.High,
            History =
            [
                new RequestHistory { RequestId = 41, ChangedById = EmployeeId, ChangedBy = employee, OldStatus = RequestStatus.Draft, NewStatus = RequestStatus.Pending, Note = "Talep gönderildi.", ChangedAt = DateTime.UtcNow }
            ]
        };

        var queryRepo = new Mock<IRequestQueryRepository>();
        queryRepo.Setup(q => q.GetDetailAsync(41, It.IsAny<CancellationToken>())).ReturnsAsync(request);

        var sut = CreateRequestService(
            new Mock<IRepository<Request>>(),
            RequestTypeRepoFor(LeaveType()),
            new Mock<IRepository<RequestHistory>>(),
            CurrentUserMock(ManagerId, UserRole.Manager),
            queryRepo: queryRepo);

        // Act
        var detail = await sut.GetDetailAsync(41);

        // Assert
        Assert.Equal("Pending", detail.Status);
        var entry = Assert.Single(detail.History);
        Assert.Equal(("Draft", "Pending"), (entry.OldStatus, entry.NewStatus));
    }

    [Theory]
    [Trait("ID", "ST-32")]
    [InlineData(OtherUserId, UserRole.Employee)]
    [InlineData(OtherUserId, UserRole.Manager)]
    public async Task GetDetailAsync_UnauthorizedCaller_ThrowsForbidden(int callerId, UserRole callerRole)
    {
        // Arrange
        var employee = new User { Id = EmployeeId, FirstName = "Ahmet", LastName = "Yılmaz", Email = "a@b.com", PasswordHash = "h", ManagerId = ManagerId };
        var request = new Request
        {
            Id = 42,
            RequesterId = EmployeeId,
            Requester = employee,
            RequestTypeId = 1,
            RequestType = LeaveType(),
            Title = "Özel talep",
            Description = "Yetkisiz erişim",
            Status = RequestStatus.Pending
        };

        var queryRepo = new Mock<IRequestQueryRepository>();
        queryRepo.Setup(q => q.GetDetailAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(request);

        var sut = CreateRequestService(
            new Mock<IRepository<Request>>(),
            RequestTypeRepoFor(LeaveType()),
            new Mock<IRepository<RequestHistory>>(),
            CurrentUserMock(callerId, callerRole),
            queryRepo: queryRepo);

        // Act & Assert
        await Assert.ThrowsAsync<ForbiddenAppException>(() => sut.GetDetailAsync(42));
    }

    [Fact]
    [Trait("ID", "ST-33")]
    public async Task GetDetailAsync_NonExistentRequest_ThrowsNotFound()
    {
        // Arrange
        var queryRepo = new Mock<IRequestQueryRepository>();
        queryRepo.Setup(q => q.GetDetailAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Request?)null);

        var sut = CreateRequestService(
            new Mock<IRepository<Request>>(),
            RequestTypeRepoFor(LeaveType()),
            new Mock<IRepository<RequestHistory>>(),
            CurrentUserMock(),
            queryRepo: queryRepo);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundAppException>(() => sut.GetDetailAsync(999));
    }

    // =========================================================================
    // 6. Defect-exposing state transition guard tests (INC-01, INC-02)
    // =========================================================================

    [Fact(Skip = "Open defect INC-01: RequestService uses OrdinalIgnoreCase against Turkish 'İzin', bypassing FR-18 for 'izin'")]
    [Trait("ID", "ST-34")]
    public async Task SubmitAsync_LeaveTypeLowercaseIzinWithoutDates_ThrowsValidationAndRemainsDraft()
    {
        // Arrange — active Leave type stored with lowercase Turkish 'i' ("izin")
        var lowercaseLeaveType = new RequestType
        {
            Id = 10,
            Name = "izin",
            RequiresApproval = true,
            IsActive = true
        };
        var existing = new Request
        {
            Id = 35,
            RequesterId = EmployeeId,
            RequestTypeId = lowercaseLeaveType.Id,
            Status = RequestStatus.Draft,
            Title = "Tarihsiz izin taslağı",
            Description = "FR-18 Türkçe büyük/küçük harf testi",
            StartDate = null,
            EndDate = null
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(35, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(lowercaseLeaveType), historyRepo, CurrentUserMock());

        // Act & Assert — FR-18 requires StartDate and EndDate for Leave requests regardless of casing
        await Assert.ThrowsAsync<ValidationAppException>(() => sut.SubmitAsync(35));

        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(Skip = "Open defect INC-02: RequestService.ValidateTypeSpecificRules only checks amount is null, bypassing FR-19 for Amount <= 0 on SubmitAsync")]
    [Trait("ID", "ST-35")]
    public async Task SubmitAsync_ExpenseDraftWithNonPositiveAmount_ThrowsValidationAndRemainsDraft()
    {
        // Arrange — existing Draft Masraf request with non-positive Amount (0)
        var existing = new Request
        {
            Id = 36,
            RequesterId = EmployeeId,
            RequestTypeId = 2,
            Status = RequestStatus.Draft,
            Title = "Sıfır tutarlı masraf taslağı",
            Description = "FR-19 pozitif tutar kontrolü",
            Amount = 0m
        };

        var requestRepo = new Mock<IRepository<Request>>();
        requestRepo.Setup(r => r.GetByIdAsync(36, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var historyRepo = new Mock<IRepository<RequestHistory>>();
        var sut = CreateRequestService(requestRepo, RequestTypeRepoFor(ExpenseType()), historyRepo, CurrentUserMock());

        // Act & Assert — FR-19 requires a strictly positive amount when submitting an Expense request
        await Assert.ThrowsAsync<ValidationAppException>(() => sut.SubmitAsync(36));

        Assert.Equal(RequestStatus.Draft, existing.Status);
        historyRepo.Verify(h => h.AddAsync(It.IsAny<RequestHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
