import {
  AfterViewChecked,
  Component,
  ElementRef,
  Input,
  ViewChild
} from '@angular/core';
import { roleLabelForProvider } from '../../core/utils/target-config';
import {
  classifyLogLine,
  formatLinesForCopy,
  formatLogTimestamp,
  LogLineClass
} from './log-line-style';

export interface ActivityLine {
  providerName: string;
  sequence: number;
  line: string;
  loggedAt?: string;
}

@Component({
  selector: 'app-live-log-panel',
  standalone: true,
  templateUrl: './live-log-panel.component.html',
  styleUrl: './live-log-panel.component.scss'
})
export class LiveLogPanelComponent implements AfterViewChecked {
  @Input() lines: ActivityLine[] = [];
  @Input() connecting = false;
  @Input() readOnly = false;
  @Input() embedded = false;
  @Input() showProviderPrefix = true;
  @Input() showToggle = true;
  @Input() title = 'Build output';

  @ViewChild('viewport') viewport?: ElementRef<HTMLElement>;

  expanded = false;
  copyLabel = 'Copy';
  private shouldStickToBottom = true;
  private lastLineCount = 0;

  ngAfterViewChecked(): void {
    if (this.lines.length !== this.lastLineCount) {
      this.lastLineCount = this.lines.length;
      if (this.shouldStickToBottom) {
        this.scrollToBottom();
      }
    }
  }

  get terminalTitle(): string {
    if (!this.showProviderPrefix || this.lines.length === 0) {
      return this.title;
    }

    return this.roleLabel(this.lines[0].providerName);
  }

  get showTerminal(): boolean {
    return this.embedded || !this.showToggle || this.expanded;
  }

  roleLabel(providerName: string): string {
    return roleLabelForProvider(providerName);
  }

  lineClass(line: string): LogLineClass {
    return classifyLogLine(line);
  }

  timestamp(loggedAt?: string): string | null {
    return formatLogTimestamp(loggedAt);
  }

  onViewportScroll(): void {
    const element = this.viewport?.nativeElement;
    if (!element) {
      return;
    }

    const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
    this.shouldStickToBottom = distanceFromBottom <= 40;
  }

  async copyLogs(): Promise<void> {
    if (this.lines.length === 0) {
      return;
    }

    const text = formatLinesForCopy(this.lines);
    try {
      await navigator.clipboard.writeText(text);
      this.copyLabel = 'Copied';
      window.setTimeout(() => {
        this.copyLabel = 'Copy';
      }, 2000);
    } catch {
      this.copyLabel = 'Failed';
      window.setTimeout(() => {
        this.copyLabel = 'Copy';
      }, 2000);
    }
  }

  private scrollToBottom(): void {
    const element = this.viewport?.nativeElement;
    if (!element) {
      return;
    }

    element.scrollTop = element.scrollHeight;
  }
}
