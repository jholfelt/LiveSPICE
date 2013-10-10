﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SyMath;
using System.ComponentModel;

namespace Circuit
{
    /// <summary>
    /// Ideal voltage source.
    /// </summary>
    [CategoryAttribute("Standard")]
    [DisplayName("Voltage Source")]
    public class VoltageSource : TwoTerminal
    {
        /// <summary>
        /// Expression for voltage V.
        /// </summary>
        private Quantity voltage = new Quantity(Call.Sin(t), Units.V);
        [Description("Voltage generated by this voltage source as a function of time t.")]
        [SchematicPersistent]
        public Quantity Voltage { get { return voltage; } set { if (voltage.Set(value)) NotifyChanged("Voltage"); } }

        public VoltageSource() { Name = "V1"; }
        
        public override void Analyze(IList<Equal> Mna, IList<Expression> Unknowns)
        {
            Expression i = Call.New(ExprFunction.New("i" + Name, t), t);
            Anode.i = i;
            Cathode.i = -i;
            Unknowns.Add(i);

            Mna.Add(Equal.New(Anode.V - Cathode.V, Voltage.Value));
        }

        protected override void DrawSymbol(SymbolLayout Sym)
        {
            int r = 10;

            Sym.AddWire(Anode, new Coord(0, r));
            Sym.AddWire(Cathode, new Coord(0, -r));

            Sym.AddCircle(EdgeType.Black, new Coord(0, 0), r);
            Sym.DrawPositive(EdgeType.Black, new Coord(0, 7));
            Sym.DrawNegative(EdgeType.Black, new Coord(0, -7));
            if (!(Voltage.Value is Constant))
                Sym.DrawFunction(
                    EdgeType.Black, 
                    (t) => t * r * 0.75,
                    (t) => Math.Sin(t * 3.1415) * r * 0.5, -1, 1);
            Sym.DrawText(Voltage.ToString(), new Point(r * 0.7, r * 0.7), Alignment.Near, Alignment.Near); 

            Sym.DrawText(Name, new Point(r * 0.7, r * -0.7), Alignment.Near, Alignment.Far); 
        }
    }
}

