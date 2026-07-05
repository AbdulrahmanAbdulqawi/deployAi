import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, signal } from '@angular/core';
import { ApiService } from '../../core/services/api.service';
import { GitHubContentDirectory } from '../../core/models/api.models';

@Component({
  selector: 'app-repo-folder-picker',
  standalone: true,
  templateUrl: './repo-folder-picker.component.html',
  styleUrl: './repo-folder-picker.component.scss'
})
export class RepoFolderPickerComponent implements OnChanges {
  @Input({ required: true }) owner!: string;
  @Input({ required: true }) repo!: string;
  @Input({ required: true }) gitRef!: string;
  @Input() initialPath = '';

  @Output() pathSelected = new EventEmitter<string>();

  readonly currentPath = signal('');
  readonly selectedPath = signal<string | null>(null);
  readonly directories = signal<GitHubContentDirectory[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  constructor(private readonly api: ApiService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['owner'] || changes['repo'] || changes['gitRef'] || changes['initialPath']) {
      this.currentPath.set(this.normalizePath(this.initialPath));
      this.selectedPath.set(this.currentPath());
      this.pathSelected.emit(this.currentPath());
      this.loadDirectories();
    }
  }

  breadcrumbs(): { label: string; path: string }[] {
    const path = this.currentPath();
    const segments = [{ label: 'Your app', path: '' }];
    if (!path) {
      return segments;
    }

    const parts = path.split('/');
    let built = '';
    for (const part of parts) {
      built = built ? `${built}/${part}` : part;
      segments.push({ label: part, path: built });
    }

    return segments;
  }

  openPath(path: string): void {
    this.currentPath.set(this.normalizePath(path));
    this.loadDirectories();
  }

  selectCurrent(): void {
    const path = this.currentPath();
    this.selectedPath.set(path);
    this.pathSelected.emit(path);
  }

  private loadDirectories(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.listRepoContents(this.owner, this.repo, this.currentPath(), this.gitRef).subscribe({
      next: (response) => {
        this.directories.set(response.directories);
        this.loading.set(false);
      },
      error: (err) => {
        this.loading.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not load folders from GitHub.');
      }
    });
  }

  private normalizePath(path: string): string {
    return path.trim().replace(/^\/+|\/+$/g, '');
  }
}
