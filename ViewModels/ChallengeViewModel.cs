using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TLIGDashboard.Models;
using TLIGDashboard.Services;

namespace TLIGDashboard.ViewModels
{
    public class ChallengeViewModel : BaseViewModel
    {
        private readonly ChallengeService _service;
        private readonly DispatcherQueue? _dispatcher;

        // Callback injected by the View for showing confirmation dialogs.
        // Returns true if the user confirms.
        public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

        // Callback injected by the View for picking a file.
        // Returns (filePath, fileName) or null if cancelled.
        public Func<Task<(string path, string name)>>? PickFileAsync { get; set; }

        // ── State ──────────────────────────────────────────────────────────
        private bool _isAdminMode = true;
        public bool IsAdminMode
        {
            get => _isAdminMode;
            set
            {
                SetField(ref _isAdminMode, value);
                OnPropertyChanged(nameof(IsStudentMode));
                RefreshChallenges();
                RaiseAllCanExecute();
            }
        }
        public bool IsStudentMode => !_isAdminMode;

        private string _currentUserId   = "DOSEN_001";
        private string _currentUserName = "Dr. Budi Santoso";

        public string CurrentUserName
        {
            get => _currentUserName;
            set => SetField(ref _currentUserName, value);
        }

        public void SetSession(string userId, string displayName, bool isAdmin)
        {
            _currentUserId   = userId;
            _currentUserName = displayName;
            IsAdminMode      = isAdmin;
        }

        // ── Collections ────────────────────────────────────────────────────
        public ObservableCollection<Challenge> Challenges { get; } = new();
        public ObservableCollection<ChallengeSubmission> CurrentSubmissions { get; } = new();
        public ObservableCollection<GradeEntry> CurrentGrades { get; } = new();

        // ── Selected items ─────────────────────────────────────────────────
        private Challenge? _selectedChallenge;
        public Challenge? SelectedChallenge
        {
            get => _selectedChallenge;
            set
            {
                SetField(ref _selectedChallenge, value);
                OnPropertyChanged(nameof(HasSelectedChallenge));
                LoadSubmissions();
                ClearEditForm();
                if (IsStudentMode) LoadMySubmission();
                RaiseAllCanExecute();
            }
        }
        public bool HasSelectedChallenge => _selectedChallenge != null;

        private ChallengeSubmission? _selectedSubmission;
        public ChallengeSubmission? SelectedSubmission
        {
            get => _selectedSubmission;
            set
            {
                SetField(ref _selectedSubmission, value);
                OnPropertyChanged(nameof(HasSelectedSubmission));
                OnPropertyChanged(nameof(FinalScore));
                LoadGrades();
                RaiseAllCanExecute();
            }
        }
        public bool HasSelectedSubmission => _selectedSubmission != null;

        public double? FinalScore =>
            _selectedSubmission != null && _selectedChallenge != null
                ? _selectedSubmission.ComputeFinalScore(_selectedChallenge)
                : null;

        // ── Form: Buat/Edit Challenge ──────────────────────────────────────
        private bool _isEditingChallenge;
        public bool IsEditingChallenge
        {
            get => _isEditingChallenge;
            set
            {
                SetField(ref _isEditingChallenge, value);
                RaiseAllCanExecute();
            }
        }

        private Challenge _challengeForm = new();
        public Challenge ChallengeForm
        {
            get => _challengeForm;
            set => SetField(ref _challengeForm, value);
        }

        // ── Form: Submission Mahasiswa ─────────────────────────────────────
        private string _submissionText = string.Empty;
        public string SubmissionText
        {
            get => _submissionText;
            set => SetField(ref _submissionText, value);
        }

        private string? _attachmentPath;
        private string? _attachmentFileName;

        public string AttachmentDisplay =>
            _attachmentFileName ?? "Belum ada file terlampir";

        // ── Form: Penilaian Dosen ──────────────────────────────────────────
        private double _gradeScore = 80;
        public double GradeScore
        {
            get => _gradeScore;
            set => SetField(ref _gradeScore, value);
        }

        private string _gradeFeedback = string.Empty;
        public string GradeFeedback
        {
            get => _gradeFeedback;
            set => SetField(ref _gradeFeedback, value);
        }

        // ── Status / Loading ───────────────────────────────────────────────
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        // ── Commands ───────────────────────────────────────────────────────
        public RelayCommand NewChallengeCommand     { get; }
        public RelayCommand SaveChallengeCommand    { get; }
        public RelayCommand CancelEditCommand       { get; }
        public RelayCommand DeleteChallengeCommand  { get; }
        public RelayCommand PublishChallengeCommand { get; }
        public RelayCommand CloseChallengeCommand   { get; }

        public RelayCommand BrowseAttachmentCommand { get; }
        public RelayCommand SubmitAnswerCommand      { get; }

        public RelayCommand GradeByDosenCommand     { get; }
        public RelayCommand GradeByAICommand         { get; }
        public RelayCommand GradeByPeerCommand       { get; }

        public RelayCommand<Challenge> EditChallengeCommand { get; }

        // ── Constructor ────────────────────────────────────────────────────
        public ChallengeViewModel(ChallengeService service, DispatcherQueue? dispatcher = null)
        {
            _service    = service;
            _dispatcher = dispatcher;

            NewChallengeCommand     = new RelayCommand(StartNewChallenge,  () => IsAdminMode);
            SaveChallengeCommand    = new RelayCommand(SaveChallenge,      () => IsAdminMode && IsEditingChallenge);
            CancelEditCommand       = new RelayCommand(CancelEdit,         () => IsEditingChallenge);
            DeleteChallengeCommand  = new RelayCommand(DeleteChallengeAsync, () => IsAdminMode && SelectedChallenge != null);
            PublishChallengeCommand = new RelayCommand(PublishChallenge,   () => IsAdminMode && SelectedChallenge?.Status == ChallengeStatus.Draft);
            CloseChallengeCommand   = new RelayCommand(CloseChallenge,     () => IsAdminMode && SelectedChallenge?.Status == ChallengeStatus.Active);

            BrowseAttachmentCommand = new RelayCommand(BrowseAttachmentAsync, () => IsStudentMode);
            SubmitAnswerCommand     = new RelayCommand(SubmitAnswer,       () => IsStudentMode && SelectedChallenge != null);

            GradeByDosenCommand     = new RelayCommand(GradeByDosen,      () => IsAdminMode && SelectedSubmission != null);
            GradeByAICommand        = new RelayCommand(async () => await GradeByAIAsync(), () => IsAdminMode && SelectedSubmission != null);
            GradeByPeerCommand      = new RelayCommand(GradeByPeer,       () => IsStudentMode && SelectedSubmission != null);

            EditChallengeCommand    = new RelayCommand<Challenge>(StartEditChallenge);

            RefreshChallenges();
        }

        private void RaiseAllCanExecute()
        {
            NewChallengeCommand.RaiseCanExecuteChanged();
            SaveChallengeCommand.RaiseCanExecuteChanged();
            CancelEditCommand.RaiseCanExecuteChanged();
            DeleteChallengeCommand.RaiseCanExecuteChanged();
            PublishChallengeCommand.RaiseCanExecuteChanged();
            CloseChallengeCommand.RaiseCanExecuteChanged();
            BrowseAttachmentCommand.RaiseCanExecuteChanged();
            SubmitAnswerCommand.RaiseCanExecuteChanged();
            GradeByDosenCommand.RaiseCanExecuteChanged();
            GradeByAICommand.RaiseCanExecuteChanged();
            GradeByPeerCommand.RaiseCanExecuteChanged();
        }

        // ── Refresh ────────────────────────────────────────────────────────
        public void RefreshChallenges()
        {
            Challenges.Clear();
            foreach (var c in _service.GetAllChallenges())
            {
                if (IsStudentMode && c.Status != ChallengeStatus.Active) continue;
                Challenges.Add(c);
            }
        }

        private void LoadSubmissions()
        {
            CurrentSubmissions.Clear();
            if (_selectedChallenge == null) return;
            foreach (var s in _service.GetSubmissions(_selectedChallenge.Id))
                CurrentSubmissions.Add(s);
        }

        private void LoadMySubmission()
        {
            var mine = CurrentSubmissions.FirstOrDefault(s => s.StudentId == _currentUserId);
            if (mine == null) return;
            SubmissionText = mine.TextAnswer;
            _attachmentPath = mine.AttachmentPath;
            _attachmentFileName = mine.AttachmentFileName;
            OnPropertyChanged(nameof(AttachmentDisplay));
        }

        private void LoadGrades()
        {
            CurrentGrades.Clear();
            if (_selectedSubmission == null) return;
            if (_selectedSubmission.DosenGrade != null)
                CurrentGrades.Add(_selectedSubmission.DosenGrade);
            if (_selectedSubmission.AIGrade != null)
                CurrentGrades.Add(_selectedSubmission.AIGrade);
            foreach (var p in _selectedSubmission.PeerGrades)
                CurrentGrades.Add(p);
        }

        // ── Public helpers called by code-behind ───────────────────────────
        public void StartNew() => StartNewChallenge();
        public void StartEdit(Challenge ch) => StartEditChallenge(ch);

        public void ApplyFormValues(string title, string description, string instructions,
            DateTime? deadline, int weightDosen, int weightAI, int weightPeer)
        {
            ChallengeForm.Title        = title;
            ChallengeForm.Description  = description;
            ChallengeForm.Instructions = instructions;
            ChallengeForm.Deadline     = deadline;
            ChallengeForm.WeightDosen  = weightDosen;
            ChallengeForm.WeightAI     = weightAI;
            ChallengeForm.WeightPeer   = weightPeer;
        }

        public void Save() => SaveChallenge();

        public void SetAttachment(string path, string name)
        {
            _attachmentPath     = path;
            _attachmentFileName = name;
            OnPropertyChanged(nameof(AttachmentDisplay));
        }

        // ── Challenge CRUD ─────────────────────────────────────────────────
        private void StartNewChallenge()
        {
            ChallengeForm = new Challenge
            {
                Id            = Guid.Empty,   // signals "new" in the form
                CreatedByName = _currentUserName,
                WeightDosen   = 50,
                WeightAI      = 25,
                WeightPeer    = 25
            };
            IsEditingChallenge = true;
        }

        private void StartEditChallenge(Challenge? ch)
        {
            if (ch == null) return;
            ChallengeForm = new Challenge
            {
                Id            = ch.Id,
                Title         = ch.Title,
                Description   = ch.Description,
                Instructions  = ch.Instructions,
                Deadline      = ch.Deadline,
                Status        = ch.Status,
                WeightDosen   = ch.WeightDosen,
                WeightAI      = ch.WeightAI,
                WeightPeer    = ch.WeightPeer,
                CreatedByName = ch.CreatedByName,
                Submissions   = ch.Submissions
            };
            IsEditingChallenge = true;
        }

        private void SaveChallenge()
        {
            if (string.IsNullOrWhiteSpace(ChallengeForm.Title))
            {
                ShowStatus("⚠ Judul challenge tidak boleh kosong.");
                return;
            }
            if (!ChallengeForm.IsWeightValid)
            {
                ShowStatus("⚠ Total bobot harus = 100 (Dosen + AI + Peer).");
                return;
            }

            bool isNew = ChallengeForm.Id == Guid.Empty || _service.GetById(ChallengeForm.Id) == null;
            if (isNew)
            {
                if (ChallengeForm.Id == Guid.Empty)
                    ChallengeForm.Id = Guid.NewGuid();
                _service.AddChallenge(ChallengeForm);
            }
            else
            {
                _service.UpdateChallenge(ChallengeForm);
            }

            IsEditingChallenge = false;
            RefreshChallenges();
            ShowStatus(isNew ? "✓ Challenge berhasil dibuat." : "✓ Challenge berhasil diperbarui.");
        }

        private void CancelEdit() => IsEditingChallenge = false;

        private async void DeleteChallengeAsync()
        {
            if (_selectedChallenge == null) return;
            if (ConfirmAsync != null)
            {
                bool confirmed = await ConfirmAsync(
                    "Hapus Challenge",
                    $"Hapus challenge \"{_selectedChallenge.Title}\"?\nSemua submission akan ikut terhapus.");
                if (!confirmed) return;
            }

            _service.DeleteChallenge(_selectedChallenge.Id);
            SelectedChallenge = null;
            RefreshChallenges();
            ShowStatus("✓ Challenge dihapus.");
        }

        private void PublishChallenge()
        {
            if (_selectedChallenge == null) return;
            _selectedChallenge.Status = ChallengeStatus.Active;
            _service.UpdateChallenge(_selectedChallenge);
            OnPropertyChanged(nameof(SelectedChallenge));
            RefreshChallenges();
            ShowStatus("✓ Challenge dipublikasikan.");
            RaiseAllCanExecute();
        }

        private void CloseChallenge()
        {
            if (_selectedChallenge == null) return;
            _selectedChallenge.Status = ChallengeStatus.Closed;
            _service.UpdateChallenge(_selectedChallenge);
            OnPropertyChanged(nameof(SelectedChallenge));
            RefreshChallenges();
            ShowStatus("✓ Challenge ditutup.");
            RaiseAllCanExecute();
        }

        private void ClearEditForm() => ChallengeForm = new();

        // ── Submission Mahasiswa ───────────────────────────────────────────
        private async void BrowseAttachmentAsync()
        {
            if (PickFileAsync == null) return;
            var result = await PickFileAsync();
            if (result == default) return;
            _attachmentPath     = result.path;
            _attachmentFileName = result.name;
            OnPropertyChanged(nameof(AttachmentDisplay));
        }

        private void SubmitAnswer()
        {
            if (_selectedChallenge == null) return;
            if (string.IsNullOrWhiteSpace(SubmissionText) && _attachmentPath == null)
            {
                ShowStatus("⚠ Isi jawaban teks atau lampirkan file.");
                return;
            }

            var existing = _service.GetSubmissions(_selectedChallenge.Id)
                .FirstOrDefault(s => s.StudentId == _currentUserId);

            if (existing != null)
            {
                existing.TextAnswer         = SubmissionText;
                existing.AttachmentPath     = _attachmentPath;
                existing.AttachmentFileName = _attachmentFileName;
                existing.SubmittedAt        = DateTime.Now;
                ShowStatus("✓ Submission diperbarui.");
            }
            else
            {
                var sub = new ChallengeSubmission
                {
                    StudentId           = _currentUserId,
                    StudentName         = _currentUserName,
                    TextAnswer          = SubmissionText,
                    AttachmentPath      = _attachmentPath,
                    AttachmentFileName  = _attachmentFileName
                };
                _service.AddSubmission(_selectedChallenge.Id, sub);
                ShowStatus("✓ Jawaban berhasil dikumpulkan.");
            }
            LoadSubmissions();
        }

        // ── Penilaian ──────────────────────────────────────────────────────
        private void GradeByDosen()
        {
            if (_selectedChallenge == null || _selectedSubmission == null) return;
            _service.GradeByDosen(_selectedChallenge.Id, _selectedSubmission.Id,
                                   GradeScore, GradeFeedback, _currentUserName);
            OnPropertyChanged(nameof(FinalScore));
            LoadGrades();
            LoadSubmissions();
            ShowStatus($"✓ Nilai dosen disimpan: {GradeScore}");
        }

        private async Task GradeByAIAsync()
        {
            if (_selectedChallenge == null || _selectedSubmission == null) return;
            IsLoading = true;
            ShowStatus("⏳ AI sedang menilai…");
            try
            {
                var grade = await _service.GradeByAIAsync(_selectedChallenge.Id, _selectedSubmission.Id);
                OnPropertyChanged(nameof(FinalScore));
                LoadGrades();
                LoadSubmissions();
                ShowStatus($"✓ Penilaian AI selesai: {grade.Score:F1}/100");
            }
            catch (Exception ex)
            {
                ShowStatus($"✗ Gagal: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void GradeByPeer()
        {
            if (_selectedChallenge == null || _selectedSubmission == null) return;
            if (_selectedSubmission.StudentId == _currentUserId)
            {
                ShowStatus("⚠ Tidak dapat menilai submission sendiri.");
                return;
            }
            try
            {
                _service.GradeByPeer(_selectedChallenge.Id, _selectedSubmission.Id,
                                      GradeScore, GradeFeedback, _currentUserName);
                OnPropertyChanged(nameof(FinalScore));
                LoadGrades();
                LoadSubmissions();
                ShowStatus($"✓ Peer review disimpan: {GradeScore}");
            }
            catch (Exception ex)
            {
                ShowStatus($"⚠ {ex.Message}");
            }
        }

        // ── Util ───────────────────────────────────────────────────────────
        private void ShowStatus(string msg)
        {
            if (_dispatcher != null)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    StatusMessage = msg;
                    _ = ClearStatusAfterDelayAsync(msg);
                });
            }
            else
            {
                StatusMessage = msg;
                _ = ClearStatusAfterDelayAsync(msg);
            }
        }

        private async Task ClearStatusAfterDelayAsync(string expected)
        {
            await Task.Delay(4000);
            if (_dispatcher != null)
                _dispatcher.TryEnqueue(() => { if (StatusMessage == expected) StatusMessage = string.Empty; });
            else
                if (StatusMessage == expected) StatusMessage = string.Empty;
        }
    }
}
