import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { switchMap, timer } from 'rxjs';
import { ImportProgressState, ImportStatusDto } from '../api/models';
import { ImportsService } from '../api/services';
import { Problem, toProblem } from '../core/problem';

const SETTLED = [ImportProgressState.Completed, ImportProgressState.CompletedWithIssues, ImportProgressState.Failed];

@Component({
  selector: 'app-imports',
  imports: [RouterLink, DatePipe, DecimalPipe],
  template: `
    <section>
      <h2>Imports</h2>
      <p class="muted">Upload an Assembly CSV export. The request returns immediately; processing runs in the background.</p>
      <div class="row">
        <input type="file" accept=".csv" (change)="pick($event)" />
        <button class="primary" [disabled]="!file() || uploading()" (click)="upload()">
          {{ uploading() ? 'Uploading…' : 'Upload' }}
        </button>
      </div>
      @if (problem(); as p) {
        <div class="alert bad">{{ p.message }} @for (e of fieldErrors(p); track e) { <span class="field-error">{{ e }}</span> }</div>
      }
    </section>

    <section>
      <h3>Import history</h3>
      <p class="muted">Progress is read from the server, so it survives a page refresh.</p>
      <table>
        <tr><th>File</th><th>Uploaded by</th><th>Uploaded at</th><th>Rows</th><th>Progress</th><th>Status</th><th></th></tr>
        @for (job of imports(); track job.id) {
          <tr>
            <td>{{ job.fileName }}</td>
            <td>{{ job.uploadedBy }}</td>
            <td>{{ job.uploadedAt | date: 'yyyy-MM-dd HH:mm:ss' }}</td>
            <td>{{ (job.totalRows ?? job.stagedRows) | number }}</td>
            <td>
              @if (!isSettled(job)) {
                <progress [value]="processed(job)" [max]="job.totalRows || job.stagedRows || 1"></progress>
                <span class="muted">{{ processed(job) | number }} {{ job.totalRows ? 'of ' + (job.totalRows | number) : 'rows read' }}</span>
              }
            </td>
            <td>
              <span class="badge" [class.ok]="job.state === 'Completed'" [class.warn]="job.state === 'CompletedWithIssues'" [class.bad]="job.state === 'Failed'">{{ job.state }}</span>
              @if (job.error) { <div class="field-error">{{ job.error }}</div> }
            </td>
            <td><a [routerLink]="['/imports', job.id]">Report</a></td>
          </tr>
        } @empty {
          <tr><td colspan="7" class="muted">No imports yet.</td></tr>
        }
      </table>
    </section>
  `,
})
export class ImportsPage implements OnInit {
  protected readonly imports = signal<ImportStatusDto[]>([]);
  protected readonly file = signal<File | null>(null);
  protected readonly uploading = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  private readonly api = inject(ImportsService);
  private readonly destroyRef = inject(DestroyRef);

  ngOnInit(): void {
    // Poll every 2 s: cheap, survives refreshes, and the list shows live progress of running imports.
    timer(0, 2000)
      .pipe(switchMap(() => this.api.apiImportsGet()), takeUntilDestroyed(this.destroyRef))
      .subscribe((jobs) => this.imports.set(jobs));
  }

  protected pick(event: Event): void {
    this.file.set((event.target as HTMLInputElement).files?.[0] ?? null);
  }

  protected upload(): void {
    const file = this.file();
    if (!file) return;
    this.uploading.set(true);
    this.problem.set(null);
    this.api.apiImportsPost({ body: { file } }).subscribe({
      next: (job) => {
        this.uploading.set(false);
        this.imports.update((jobs) => [job, ...jobs.filter((j) => j.id !== job.id)]);
      },
      error: (error) => {
        this.uploading.set(false);
        this.problem.set(toProblem(error));
      },
    });
  }

  protected isSettled(job: ImportStatusDto): boolean {
    return SETTLED.includes(job.state!);
  }

  protected processed(job: ImportStatusDto): number {
    return job.phase === 'Classifying' ? job.classifiedRows ?? 0 : job.stagedRows ?? 0;
  }

  protected fieldErrors(p: Problem): string[] {
    return Object.values(p.fieldErrors).flat();
  }
}
