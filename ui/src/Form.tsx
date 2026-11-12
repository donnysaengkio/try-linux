// The Settings page's building blocks: sections that open and close, labelled rows, notes, and the controls
// (Base UI's Checkbox, Radio, Select, NumberField and ToggleGroup), drawn in Try Linux's style.
import { Checkbox } from "@base-ui/react/checkbox";
import { Collapsible } from "@base-ui/react/collapsible";
import { NumberField } from "@base-ui/react/number-field";
import { Radio } from "@base-ui/react/radio";
import { RadioGroup } from "@base-ui/react/radio-group";
import { Select } from "@base-ui/react/select";
import { Toggle } from "@base-ui/react/toggle";
import { ToggleGroup } from "@base-ui/react/toggle-group";
import { createContext, useContext, type ReactNode } from "react";
import { Icon } from "./Icon";

// Which sections are open (kept while Settings is open), and the one opened last, the only one highlighted.
export interface Sections { open: Set<string>; last: string | null; toggle: (title: string, open: boolean) => void }
export const SectionsContext = createContext<Sections>({ open: new Set(), last: null, toggle: () => {} });

// Each word starts with a capital letter, as on the other editions ("Camera And USB").
const titleCase = (t: string) => t.split(" ").map(w => w && w[0].toUpperCase() + w.slice(1)).join(" ");

// A bar that opens and closes its section; closed, it shows a summary of what's inside.
export function Section({ title, summary, children }: { title: string; summary: string; children: ReactNode }) {
  const s = useContext(SectionsContext);
  const open = s.open.has(title);
  return (
    <Collapsible.Root open={open} onOpenChange={o => s.toggle(title, o)} className="section">
      <Collapsible.Trigger className={"section-bar" + (open && s.last === title ? " lit" : "")}>
        <span className="section-title">{titleCase(title)}</span>
        <span className="section-summary">{open ? "" : summary}</span>
        <span className="section-chev"><Icon name={open ? "chevron-down" : "chevron-right"} /></span>
      </Collapsible.Trigger>
      <Collapsible.Panel className="section-body">
        <div className="section-inner">{children}</div>
      </Collapsible.Panel>
    </Collapsible.Root>
  );
}

export function SubHeading({ children }: { children: ReactNode }) {
  return <div className="subheading">{children}</div>;
}

// A label on the left and its control on the right; `inset` lines it up with a checkbox's text above it.
export function Row({ label, children, inset, top }: { label: string; children: ReactNode; inset?: boolean; top?: boolean }) {
  return (
    <div className={"row" + (top ? " top" : "")}>
      <span className={"row-label" + (inset ? " inset" : "")}>{label}</span>
      <div className="row-field">{children}</div>
    </div>
  );
}

export function Note({ children, inset }: { children: ReactNode; inset?: boolean }) {
  return <p className={"note" + (inset ? " inset" : "")}>{children}</p>;
}

// A sentence with its button on the right (Recovery, Disk Utilities).
export function Action({ text, label, onClick, danger, disabled }:
  { text: string; label: string; onClick: () => void; danger?: boolean; disabled?: boolean }) {
  return (
    <div className="action-row">
      <span>{text}</span>
      <button className={"btn small action" + (danger ? " danger" : "")} disabled={disabled} onClick={onClick}>{label}</button>
    </div>
  );
}

const CHECK = <svg viewBox="0 0 24 24" className="icon" aria-hidden="true"><path d="M20 6 9 17l-5-5" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" /></svg>;

export function Check({ label, checked, onChange, disabled }:
  { label: string; checked: boolean; onChange?: (v: boolean) => void; disabled?: boolean }) {
  return (
    <label className={"check-row" + (disabled ? " disabled" : "")}>
      <Checkbox.Root className="check" checked={checked} disabled={disabled} onCheckedChange={v => onChange?.(v)}>
        <Checkbox.Indicator className="check-mark">{CHECK}</Checkbox.Indicator>
      </Checkbox.Root>
      {label}
    </label>
  );
}

export function Radios<T extends string>({ value, options, onChange }:
  { value: T; options: [T, string][]; onChange: (v: T) => void }) {
  return (
    <RadioGroup className="radios" value={value} onValueChange={v => onChange(v as T)}>
      {options.map(([v, label]) => (
        <label key={v} className="radio-row">
          <Radio.Root className="radio" value={v}><Radio.Indicator className="radio-dot" /></Radio.Root>
          {label}
        </label>
      ))}
    </RadioGroup>
  );
}

export function Choice<T extends string>({ value, options, onChange, disabled, width }:
  { value: T; options: { value: T; label: string }[]; onChange?: (v: T) => void; disabled?: boolean; width?: number }) {
  return (
    <Select.Root items={options} value={value} disabled={disabled} onValueChange={v => v !== null && onChange?.(v as T)}>
      <Select.Trigger className="select" style={{ width }}>
        <Select.Value className="select-value" />
        <Select.Icon className="select-icon"><Icon name="chevron-down" /></Select.Icon>
      </Select.Trigger>
      <Select.Portal>
        <Select.Positioner className="menu-positioner" sideOffset={4} alignItemWithTrigger={false} align="start">
          <Select.Popup className="menu">
            {options.map(o => (
              <Select.Item key={o.value} value={o.value} className="menu-item select-item">
                <Select.ItemText>{o.label}</Select.ItemText>
                <Select.ItemIndicator className="select-tick">{CHECK}</Select.ItemIndicator>
              </Select.Item>
            ))}
          </Select.Popup>
        </Select.Positioner>
      </Select.Portal>
    </Select.Root>
  );
}

// A whole number with − and + buttons; arrow keys and the mouse wheel step it too.
export function NumberBox({ value, min, max, step = 1, onChange, disabled }:
  { value: number; min: number; max: number; step?: number; onChange: (v: number) => void; disabled?: boolean }) {
  return (
    <NumberField.Root className="number" value={value} min={min} max={max} step={step} disabled={disabled}
      allowWheelScrub onValueChange={v => v !== null && onChange(v)}>
      <NumberField.Group className="number-group">
        <NumberField.Decrement className="number-step" aria-label="Less">−</NumberField.Decrement>
        <NumberField.Input className="number-input" />
        <NumberField.Increment className="number-step" aria-label="More">+</NumberField.Increment>
      </NumberField.Group>
    </NumberField.Root>
  );
}

// Buttons in a row, one chosen (the resource profile).
export function Segmented({ value, options, onChange }: { value: number; options: string[]; onChange: (i: number) => void }) {
  return (
    <ToggleGroup className="segmented" value={[String(value)]}
      onValueChange={v => { if (v.length) onChange(Number(v[v.length - 1])); }}>
      {options.map((label, i) => <Toggle key={label} value={String(i)} className="segment">{label}</Toggle>)}
    </ToggleGroup>
  );
}
