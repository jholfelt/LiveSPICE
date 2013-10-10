﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SyMath;
using System.ComponentModel;

namespace Circuit
{
    /// <summary>
    /// Implements an ideal operational amplifier (op-amp). An ideal op-amp will not saturate.
    /// </summary>
    [CategoryAttribute("Standard")]
    [DisplayName("Ideal Op-Amp")]
    public class IdealOpAmp : Component
    {
        protected Terminal p, n, o;
        public override IEnumerable<Terminal> Terminals 
        { 
            get 
            {
                yield return p;
                yield return n;
                yield return o;
            } 
        }
        [Browsable(false)]
        public Terminal Positive { get { return p; } }
        [Browsable(false)]
        public Terminal Negative { get { return n; } }
        [Browsable(false)]
        public Terminal Out { get { return o; } }

        public IdealOpAmp()
        {
            p = new Terminal(this, "+");
            n = new Terminal(this, "-");
            o = new Terminal(this, "Out");
        }

        public override void Analyze(IList<Equal> Mna, IList<Expression> Unknowns)
        {
            // Infinite input impedance.
            Positive.i = Constant.Zero;
            Negative.i = Constant.Zero;

            // Unknown output current.
            Out.i = Call.New(ExprFunction.New("i" + Name, t), t);
            Unknowns.Add(Out.i);

            // The voltage between the positive and negative terminals is 0.
            Mna.Add(Equal.New(Positive.V, Negative.V));
        }

        public override void LayoutSymbol(SymbolLayout Sym)
        {
            Sym.AddTerminal(Positive, new Coord(-30, 10));
            Sym.AddTerminal(Negative, new Coord(-30, -10));
            Sym.AddTerminal(Out, new Coord(30, 0));

            Sym.AddWire(Positive, new Coord(-20, 10));
            Sym.AddWire(Negative, new Coord(-20, -10));
            Sym.AddWire(Out, new Coord(20, 0));

            Sym.DrawPositive(EdgeType.Black, new Coord(-17, 10));
            Sym.DrawNegative(EdgeType.Black, new Coord(-17, -10));

            Sym.AddLoop(EdgeType.Black,
                new Coord(-20, 20),
                new Coord(-20, -20),
                new Coord(20, 0));

            Sym.DrawText(Name, new Coord(0, -10), Alignment.Near, Alignment.Far);
        }
    }
}
