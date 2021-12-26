export const formatterNoDateAndTime = (ds: Date | string): string => {
    const date = new Date(ds)
    return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()} ${date.getHours()}:${date.getMinutes()}:${date.getSeconds()}`
}

export const formatterNoDateOnly = Intl.DateTimeFormat("nb-NO", {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit'
})