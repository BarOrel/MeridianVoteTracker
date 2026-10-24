import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ConflictDecision, ConflictDto, ConflictStatus, ConflictSummaryDto } from '../api/models';
import { ConflictsService } from '../api/services';
import { Problem, toProblem } from '../core/problem';

const BALLOTS: Record<string, string> = { Y: 'YES', N: 'NO', A: 'ABSTAIN', X: 'ABSENT' };

@Component({
  selector: 'app-review',
  imports: [DatePipe, RouterLink],
  template: `
    <section>
      <h2>Review</h2>
      <p class="muted">Items waiting for a human decision. Approved data never changes without one.</p>
    </section>
    <div class="layout">
      <section>
        <div class="row">
          @for (s of statuses; track s) {
            <button [class.primary]="status() === s" (click)="setStatus(s)">{{ s }}</button>
          }
        </div>
        <input placeholder="Filter by vote (e.g. MA/2024/003)…" (input)="voteFilter.set($any($event.target).value)" style="width:100%" />
        <table>
          @for (c of filtered(); track c.id) {
            <tr class="clickable" [class.selected]="selected()?.id === c.id" (click)="open(c.id!)">
              <td><b>{{ c.assemblyVoteId }}</b><div class="muted">{{ describe(c) }}</div></td>
              <td><span class="badge" [class.warn]="c.status === 'Pending'" [class.ok]="c.status === 'Accepted'">{{ c.status }}</span></td>
            </tr>
          } @empty {
            <tr><td class="muted">Nothing here.</td></tr>
          }
        </table>
      </section>

      <section>
        @if (selected(); as c) {
          <h3>
            {{ c.kind === 'CountryBallot' ? 'Country vote conflict' : 'Vote details conflict' }} ·
            <a [routerLink]="['/votes', c.voteId]">{{ c.assemblyVoteId }}</a> · {{ c.stateName ?? c.field }} {{ c.stateCode ? '(' + c.stateCode + ')' : '' }}
          </h3>
          <p class="muted">{{ c.voteTitle }} · Session {{ c.session }} · {{ c.voteDate }}</p>

          @if (notice(); as n) {
            <div class="alert"><b>{{ n }}</b></div>
          }

          <div class="side-by-side">
            <div class="box approved">
              <div class="muted">{{ c.status === 'Accepted' ? 'PREVIOUS APPROVED VALUE' : 'APPROVED VALUE' }}</div>
              <div class="value">{{ show(c, c.approvedValueAtDetection) }}</div>
              <div>{{ c.approvedSource?.fileName }}</div>
              <div class="muted">uploaded {{ c.approvedSource?.uploadedAt | date: 'yyyy-MM-dd HH:mm' }} by {{ c.approvedSource?.uploadedBy }}</div>
              @if (c.approvedSource?.publishedAt) { <div class="muted">published by the Assembly {{ c.approvedSource?.publishedAt | date: 'yyyy-MM-dd' }}</div> }
            </div>
            <div class="box proposed">
              <div class="muted">INCOMING VALUE</div>
              <div class="value">{{ show(c, c.proposedValue) }}</div>
              <div>{{ c.proposedSource?.fileName }} <span class="muted">(row {{ c.proposedSource?.rowNumber }})</span></div>
              <div class="muted">uploaded {{ c.proposedSource?.uploadedAt | date: 'yyyy-MM-dd HH:mm' }} by {{ c.proposedSource?.uploadedBy }}</div>
              @if (c.proposedSource?.publishedAt) { <div class="muted">published by the Assembly {{ c.proposedSource?.publishedAt | date: 'yyyy-MM-dd' }}</div> }
            </div>
          </div>
          <p>Current approved value: <b>{{ show(c, c.currentApprovedValue) }}</b>.
            <span class="muted">Published snapshots never change; new snapshots use the approved value.</span></p>

          @if (c.status === 'Pending') {
            <label>Comment (optional)<textarea [value]="comment()" (input)="comment.set($any($event.target).value)"></textarea></label>
            @if (problem(); as p) { <div class="alert bad">{{ p.message }}</div> }
            <div class="row">
              <button class="primary" [disabled]="busy()" (click)="decide(c, 'Accept')">Accept incoming value</button>
              <button class="danger" [disabled]="busy()" (click)="decide(c, 'Reject')">Reject, keep approved</button>
            </div>
            <p class="muted">Every decision is recorded with who decided, when and the comment.</p>
          } @else {
            <p><span class="badge">{{ c.status }}</span> by <b>{{ c.resolvedBy }}</b> at {{ c.resolvedAt | date: 'yyyy-MM-dd HH:mm:ss' }}</p>
            @if (c.resolutionComment) { <p>“{{ c.resolutionComment }}”</p> }
            @if (c.supersededByConflictId) { <p class="muted">Another proposal for the same value was accepted first.</p> }
          }
        } @else {
          <p class="muted">Select an item.</p>
        }
      </section>
    </div>
  `,
})
export class ReviewPage implements OnInit {
  protected readonly statuses = Object.values(ConflictStatus);
  protected readonly status = signal<ConflictStatus>(ConflictStatus.Pending);
  protected readonly conflicts = signal<ConflictSummaryDto[]>([]);
  protected readonly voteFilter = signal('');
  protected readonly filtered = computed(() =>
    this.conflicts().filter((c) => (c.assemblyVoteId ?? '').toLowerCase().includes(this.voteFilter().trim().toLowerCase())),
  );
  protected readonly selected = signal<ConflictDto | null>(null);
  protected readonly comment = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly notice = signal<string | null>(null);
  private readonly api = inject(ConflictsService);

  ngOnInit(): void {
    this.load();
  }

  protected setStatus(status: ConflictStatus): void {
    this.status.set(status);
    this.load();
  }

  protected open(id: string): void {
    this.notice.set(null);
    this.problem.set(null);
    this.comment.set('');
    this.api.apiConflictsIdGet({ id }).subscribe((c) => this.selected.set(c));
  }

  protected decide(conflict: ConflictDto, decision: 'Accept' | 'Reject'): void {
    this.busy.set(true);
    this.problem.set(null);
    this.api
      .apiConflictsIdResolutionPost({ id: conflict.id!, body: { decision: decision as ConflictDecision, comment: this.comment() || null } })
      .subscribe({
        next: (updated) => {
          this.busy.set(false);
          this.selected.set(updated);
          this.notice.set(decision === 'Accept' ? 'Accepted: the incoming value is now approved.' : 'Rejected: the approved value was kept.');
          this.load();
        },
        error: (error) => {
          this.busy.set(false);
          const problem = toProblem(error);
          if (problem.status === 409 && problem.current) {
            // CON-05: someone else decided first. Show the current state instead of a generic error.
            const current = problem.current as ConflictDto;
            this.selected.set(current);
            this.notice.set(
              current.status === 'Pending'
                ? problem.message
                : `${current.resolvedBy} already resolved this conflict (${current.status}). Your decision (${decision}) was not applied. The current state is shown.`,
            );
            this.load();
          } else {
            this.problem.set(problem);
          }
        },
      });
  }

  protected describe(c: ConflictSummaryDto): string {
    const label = c.kind === 'CountryBallot' ? `${c.stateCode} country vote` : `${c.field} changed`;
    return `${label}: ${c.approvedValue} → ${c.proposedValue}`;
  }

  protected show(c: ConflictDto, value?: string | null): string {
    return c.kind === 'CountryBallot' ? BALLOTS[value ?? ''] ?? value ?? '' : value ?? '';
  }

  private load(): void {
    this.api.apiConflictsGet({ status: this.status() }).subscribe((list) => this.conflicts.set(list));
  }
}
