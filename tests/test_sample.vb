Imports System
Imports System.Collections.Generic
Imports System.Linq

Namespace ГК.СКС.Расчёт

    Public Class РасчётЗарплаты
        Inherits БазовыйРасчёт
        Implements IРасчёт, IВалидатор

        Private _коэффициент As Decimal

        Public Property Коэффициент As Decimal
            Get
                Return _коэффициент
            End Get
            Set(value As Decimal)
                _коэффициент = value
            End Set
        End Property

        Public Sub New(коэф As Decimal)
            _коэффициент = коэф
        End Sub

        Public Function РассчитатьОклад(часы As Integer) As Decimal
            Return часы * _коэффициент
        End Function

        Public Function РассчитатьПремию(оклад As Decimal) As Decimal
            Return оклад * 0.15D
        End Function

        Private Sub ВалидироватьДанные(сотрудник As String)
            If String.IsNullOrEmpty(сотрудник) Then
                Throw New ArgumentException("Имя не может быть пустым")
            End If
        End Sub

        Public Event РасчётЗавершён(sender As Object, e As EventArgs)

    End Class

    Public Module ВспомогательныеФункции

        Public Function ФорматСуммы(сумма As Decimal) As String
            Return сумма.ToString("N2")
        End Function

        Public Sub ЗаписатьЛог(сообщение As String)
            Console.WriteLine(сообщение)
        End Sub

    End Module

    Public Interface IРасчёт
        Function РассчитатьОклад(часы As Integer) As Decimal
        Function РассчитатьПремию(оклад As Decimal) As Decimal
    End Interface

    Public Enum СтатусРасчёта
        Новый
        ВОбработке
        Завершён
        Ошибка
    End Enum

    Public Structure ПериодРасчёта
        Public Начало As Date
        Public Конец As Date
        Public Property ДниВПериоде As Integer
    End Structure

End Namespace
