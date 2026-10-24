import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { switchMap, timer } from 'rxjs';
import { SnapshotSummaryDto } from '../api/models';
import { SnapshotsService } from '../api/services';
import { CurrentUser } from '../core/current-user';
import { Problem, toProblem } from '../core/problem';

@Component({
  selector: 'app-snapshots',
  imports: [RouterLink, DatePipe],
  template: `
    <div class="layout">
      <section>
        <h2>Create snapshot</h2>
        <label>Name <input style="width:100%" [value]="name()" (input)="name.set($any($event.target).value)" /></label>
        @if (problem()?.fieldErrors?.['name']; as e) { <div class="field-error">{{ e.join(' ') }}</div> }
        <div class="row">From <input type="date" (change)="from = $any($event.target).value || undefined" />
          To <input type="date" (change)="to = $any($event.target).value || undefined" /></div>
        <div class="row">Session <input type="number" style="width:80px" (change)="session = $any($event.target).value || undefined" /></div>
        <label>and/or vote ids (comma separated)
          <textarea (input)="list = $any($event.target).value"></textarea></label>
        @if (problem()?.fieldErrors?.['selection']; as e) { <div class="alert bad">{{ e.join(' ') }}</div> }
        @if (problem(); as p) { @if (!p.fieldErrors['selection'] && !p.fieldErrors['name']) { <div class="alert bad">{{ p.message }}</div> } }
        <button class="primary" (click)="create()">Create snapshot</button>
        <p class="muted">The calculation runs in the background. A saved snapshot never changes.</p>
      </section>

      <section>
        <h2>All snapshots</h2>
        <table>
          <tr><th>Name</th><th>Votes</th><th>Created</th><th>Status</th><th></th></tr>
          @for (s of snapshots(); track s.id) {
            <tr>
              <td><a [routerLink]="['/snapshots', s.id]">{{ s.name }}</a><div class="muted">{{ s.selection }}</div></td>
              <td>{{ s.votes }}</td>
              <td>{{ s.createdBy }} · {{ s.createdAt | date: 'yyyy-MM-dd HH:mm' }}</td>
              <td>
                @if (s.publishedQuarter) { <span class="badge ok">Published {{ s.publishedQuarter }}</span> }
                @else { <span class="badge" [class.bad]="s.status === 'Failed'">{{ s.status === 'Pending' ? 'Calculating' : s.status }}</span> }
                @if (s.error) { <div class="field-error">{{ s.error }}</div> }
              </td>
              <td>
                @if (currentUser.isAdmin() && s.status === 'Completed' && !s.publishedQuarter) {
                  <input placeholder="2024-Q1" style="width:80px" #q /> <button (click)="publish(s, q.value)">Mark as published</button>
                }
              </td>
            </tr>
          } @empty { <tr><td colspan="5" class="muted">No snapshots yet.</td></tr> }
        </table>
        @if (publishProblem(); as p) { <div class="alert bad">{{ p.message }}</div> }
        <p class="muted">Admins can mark one snapshot per quarter as published.</p>
      </section>
    </div>
  `,
})
export class SnapshotsPage implements OnInit {
  protected readonly snapshots = signal<SnapshotSummaryDto[]>([]);
  protected readonly name = signal('');
  protected readonly problem = signal<Problem | null>(null);
  protected readonly publishProblem = signal<Problem | null>(null);
  protected readonly currentUser = inject(CurrentUser);
  protected from?: string;
  protected to?: string;
  protected session?: number;
  protected list = '';
  private readonly api = inject(SnapshotsService);
  private readonly destroyRef = inject(DestroyRef);

  ngOnInit(): void {
    timer(0, 2000)
      .pipe(switchMap(() => this.api.apiSnapshotsGet()), takeUntilDestroyed(this.destroyRef))
      .subscribe((s) => this.snapshots.set(s));
  }

  protected create(): void {
    this.problem.set(null);
    const ids = this.list.split(',').map((x) => x.trim()).filter((x) => x.length > 0);
    this.api
      .apiSnapshotsPost({
        body: { name: this.name(), from: this.from, to: this.to, session: this.session ? Number(this.session) : null, assemblyVoteIds: ids.length ? ids : null },
      })
      .subscribe({
        next: (s) => this.snapshots.update((all) => [s.snapshot!, ...all]),
        error: (e) => this.problem.set(toProblem(e)),
      });
  }

  protected publish(s: SnapshotSummaryDto, quarter: string): void {
    this.publishProblem.set(null);
    this.api.apiSnapshotsIdPublishPost({ id: s.id!, body: { quarter } }).subscribe({
      next: () => this.api.apiSnapshotsGet().subscribe((all) => this.snapshots.set(all)),
      error: (e) => {
        const p = toProblem(e);
        this.publishProblem.set(p.fieldErrors['quarter'] ? { ...p, message: p.fieldErrors['quarter'].join(' ') } : p);
      },
    });
  }
}
