import { HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { UserDto } from '../api/models';

/** Simplified identity (assignment §3): the selected pre-defined user is sent as X-User-Id; the API enforces roles. */
@Injectable({ providedIn: 'root' })
export class CurrentUser {
  private static readonly storageKey = 'meridian.userId';
  readonly id = signal<string>(CurrentUser.load());
  readonly user = signal<UserDto | null>(null);

  select(user: UserDto): void {
    this.user.set(user);
    this.id.set(user.id ?? 'dana');
    try {
      localStorage.setItem(CurrentUser.storageKey, this.id());
    } catch {
      /* storage unavailable: the selection lasts for this page only */
    }
  }

  isAdmin(): boolean {
    return this.user()?.role === 'Admin';
  }

  private static load(): string {
    try {
      return localStorage.getItem(CurrentUser.storageKey) ?? 'dana';
    } catch {
      return 'dana';
    }
  }
}

export const userHeaderInterceptor: HttpInterceptorFn = (request, next) =>
  next(request.clone({ setHeaders: { 'X-User-Id': inject(CurrentUser).id() } }));
